using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.AI.Graphs;
using Il2CppDrova.AI.Modules;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps creatures thinking when they are near any player, not just the local one.
    /// Patches both distance gates: <c>ManualGraphUpdater</c>'s tick rate and the trees' <c>IsPlayerInRangeCondition</c>.
    /// </summary>
    internal static class EnemyPerception
    {
        /// <summary>
        /// The game's own idling distance, measured from every player.
        /// </summary>
        private const float PlayerRange = 1000f;

        private static bool _reported;

        // Re-running the condition re-enters this patch.
        private static bool _reentering;

        /// <summary>
        /// How many times the game asked whether a creature is near the player. Zero means the patch never ran (inlined).
        /// </summary>
        internal static int Asked { get; private set; }

        /// <summary>How many times a creature was kept awake for a remote player. For <c>coop_status</c>.</summary>
        internal static int Extended { get; private set; }

        /// <summary>How many times a behavior tree asked whether the player is in range. For <c>coop_status</c>.</summary>
        internal static int AskedCondition { get; private set; }

        /// <summary>How many times that question was answered for a remote player. For <c>coop_status</c>.</summary>
        internal static int ExtendedCondition { get; private set; }

        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(ManualGraphUpdater), "IsPlayerInRange",
                typeof(EnemyPerception), nameof(IsPlayerInRangePostfix));

            Hooking.TryPostfix(harmony, typeof(IsPlayerInRangeCondition), "OnCheck",
                typeof(EnemyPerception), nameof(OnCheckPostfix));
        }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            Asked = 0;
            Extended = 0;
            AskedCondition = 0;
            ExtendedCondition = 0;
        }

        /// <summary>
        /// Turns a no into a yes for a creature near a remote player, never the other way.
        /// </summary>
        private static void IsPlayerInRangePostfix(ManualGraphUpdater __instance, ref bool __result)
        {
            try
            {
                Asked++;

                if (__result) return;

                if (!CoopSession.IsLive) return;

                if (RemotePlayers.Count == 0) return;

                Actor actor = __instance._actor;
                if (actor == null) return;

                if (!RemotePlayers.IsAnyWithin(actor.GetFeetPosition(), PlayerRange)) return;

                __result = true;
                Extended++;

                Announce();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] deciding whether a creature is near a player failed: " + e);
            }
        }

        /// <summary>
        /// Re-runs the tree's own range condition once per remote player, keeping its hysteresis and facing bonus.
        /// The player field is always restored, since it is assigned only once per world load.
        /// </summary>
        private static void OnCheckPostfix(IsPlayerInRangeCondition __instance, ref bool __result)
        {
            if (_reentering) return;

            Actor? restore = null;

            try
            {
                AskedCondition++;

                if (__result) return;

                if (!CoopSession.IsLive) return;
                if (RemotePlayers.Count == 0) return;

                restore = __instance._playerActor;
                if (restore == null) return;

                _reentering = true;

                foreach (KeyValuePair<Messages.PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
                {
                    Actor other = body.Value.Body;
                    if (other == null) continue;

                    __instance._playerActor = other;

                    if (!__instance.OnCheck()) continue;

                    __result = true;
                    ExtendedCondition++;

                    Announce();
                    return;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] asking a behavior tree about a remote player failed: " + e);
            }
            finally
            {
                _reentering = false;

                if (restore != null)
                {
                    __instance._playerActor = restore;
                }
            }
        }

        private static void Announce()
        {
            if (_reported) return;

            _reported = true;

            MelonLogger.Msg("[Enemy] a creature was kept awake because a remote player is standing near " +
                "it. Without this the far end of a session is a world that has stopped thinking.");
        }
    }
}
