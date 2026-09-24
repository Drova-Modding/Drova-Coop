using Drova_Modding_API;
using Il2CppDrova;
using Il2CppDrova.AI;
using Il2CppDrova.AI.Logic;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes an enemy pick a new target when the player it was fighting goes down.
    ///
    /// Drova keeps a target until the actor holding it stops existing.
    /// <c>ThreatTargetActorService.GetCurrentTargetActor</c> caches its choice and only chooses again
    /// when the cached one fails <c>IAIActor.IsValid</c> - which is <c>_actor != null</c> and nothing
    /// else. A knocked-out actor is still an actor, so the cache survives it. That is not an oversight
    /// in a game with one player: dying there ends the run, so there is never a body on the floor that
    /// the fight has to move on from.
    ///
    /// Co-op creates exactly that body. A downed player would otherwise be beaten on the floor by
    /// everything that was already fighting them, while the teammate still standing is ignored - and
    /// since being helped up needs somebody to stand over you, the fight has to let go of the person on
    /// the ground for the recovery to be reachable at all.
    ///
    /// Clearing the cache is all this does. The choice is then made again by the game's own chain, whose
    /// <c>TargetIsAliveDecorator</c> already discards anything knocked out - so the next target is
    /// whoever the game would have picked had the downed player never been there.
    ///
    /// Only bodies with a player behind them. An NPC knocked out in a brawl keeps its attacker's
    /// attention exactly as it did before, because that is a fight the game authored and this has no
    /// business changing.
    /// </summary>
    internal static class DownedTargeting
    {
        private static bool _reported;

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(ThreatTargetActorService), "GetCurrentTargetActor",
                typeof(DownedTargeting), nameof(GetCurrentTargetActorPrefix));
        }

        /// <summary>
        /// Drops a cached target that is lying on the floor, just before the service would have returned
        /// it.
        ///
        /// A prefix rather than a postfix, so the original does the choosing: nulling the cache is the
        /// whole instruction, and the method that runs next is the game's own answer to what should
        /// happen when there is no cache.
        /// </summary>
        private static void GetCurrentTargetActorPrefix(ThreatTargetActorService __instance)
        {
            try
            {
                if (__instance == null) return;

                IAIActor cached = __instance._currentCachedTarget;
                if (cached == null) return;

                Actor? target = cached.GetObject()?.TryCast<Actor>();
                if (target == null) return;

                if (target.CustomStats == null || !target.CustomStats.IsKnockout) return;
                if (!RemotePlayers.IsPlayersBody(target)) return;

                __instance._currentCachedTarget = null;

                // The first one only. What is worth having in a log is that the patch runs at all - a
                // body on the floor is attacked by everything that was fighting it, so this fires many
                // times for one knockout and a line each would bury the session.
                if (!_reported)
                {
                    _reported = true;
                    MelonLogger.Msg("[Aggro] an enemy let go of a player who had gone down and chose again.");
                }
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside the AI's own tick, and an exception let out would take the
                // enemy's whole decision with it rather than only its choice of target.
                MelonLogger.Error("[Aggro] letting go of a downed target failed: " + e);
            }
        }
    }
}
