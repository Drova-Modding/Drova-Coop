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

                for (int index = 0; index < known.Count; index++)
                {
                    ICognitionObject candidate = known[index];
                    if (candidate == null) continue;

                    // A destroyed actor reports float.MaxValue, so it fails the range test.
                    Vector2 where = candidate.Position;

                    if ((where - position).sqrMagnitude > radius * radius) continue;
                    if (!RemotePlayers.IsAnyWithin(where, keep)) continue;

                    // Add returns false for anything the game already returned.
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

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
