using Drova_Modding_API;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova.Cognition;
using Il2CppDrova.Utilities.LazyLoading;
using Il2CppGilzoide.UpdateManager.Internal;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Lets a creature notice a remote player standing next to it. <c>CognitionOctreeManager</c> only returns
    /// cognition objects near the local player, so this adds back the ones near a remote player.
    /// It does not gate targeting, which <c>IsPerceivingActor</c> checks directly.
    /// </summary>
    internal sealed class EnemyCognition : ICoopSystem
    {
        // Candidates near a remote player, gathered on a frame's first query. The game's own positions are per frame too.
        private static readonly List<ICognitionObject> NearRemote = new();
        private static int _nearFrame = -1;
        private static IntPtr _nearOwner;
        private static int _nearKnownCount;
        private static float _nearKeep;

        private static bool _reported;

        /// <summary>Objects made perceivable because a remote player is near them.</summary>
        internal static int Restored { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // Patched here, not on the private RebuildTree that holds the test, because RebuildTree is inlined.
            Hooking.TryPostfix(harmony, typeof(CognitionOctreeManager), "GetCognitionObjects",
                typeof(EnemyCognitionPatches), nameof(EnemyCognitionPatches.GetCognitionObjectsPostfix));
        }

        private static void Reset()
        {
            Restored = 0;
            NearRemote.Clear();
            _nearFrame = -1;
        }

        internal static void GetCognitionObjectsPostfix(
            CognitionOctreeManager __instance, Vector2 position, float radius,
            FastRemoveList<ICognitionObject>? __result)
        {
            try
            {
                if (__result == null) return;

                if (!CoopSession.IsLive) return;

                if (RemotePlayers.Count == 0) return;

                FastRemoveList<ICognitionObject> known = __instance._activeCognitions;
                if (known == null || known.Count == 0) return;

                // The game's own range, which collectors raise each frame, so a second player is perceived on the same terms.
                float keep = __instance._cognitionActivePlayerInRange;

                if (!RemotePlayers.IsAnyWithin(position, radius + keep)) return;

                GatherNearRemote(__instance, known, keep);

                foreach (ICognitionObject candidate in NearRemote)
                {
                    // A destroyed actor reports float.MaxValue, so it fails the range test.
                    Vector2 where = candidate.Position;

                    if ((where - position).sqrMagnitude > radius * radius) continue;

                    if (!__result.Add(candidate)) continue;

                    Restored++;

                    if (!_reported)
                    {
                        _reported = true;
                        MelonLogger.Msg("[Enemy] a creature was allowed to notice something because a remote " +
                            "player is standing near it. Without this the far end of a session is a world " +
                            "where nothing can be fought.");
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] widening what a creature can notice failed: " + e);
            }
        }

        private static void GatherNearRemote(CognitionOctreeManager owner, FastRemoveList<ICognitionObject> known, float keep)
        {
            int frame = Time.frameCount;

            // A changed count means an object came or went since the gather.
            if (_nearFrame == frame && _nearOwner == owner.Pointer && _nearKnownCount == known.Count && _nearKeep == keep) return;

            _nearFrame = frame;
            _nearOwner = owner.Pointer;
            _nearKnownCount = known.Count;
            _nearKeep = keep;
            NearRemote.Clear();

            for (int index = 0; index < known.Count; index++)
            {
                ICognitionObject candidate = known[index];
                if (candidate == null) continue;

                if (RemotePlayers.IsAnyWithin(candidate.Position, keep)) NearRemote.Add(candidate);
            }
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
