using Drova_Modding_API;
using Il2CppDrova;
using Il2CppDrova.AI;
using Il2CppDrova.AI.Logic;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes an enemy pick a new target when the player body it was fighting goes down.
    /// GetCurrentTargetActor keeps its cache while the actor exists, so a knocked-out player stays targeted otherwise.
    /// </summary>
    internal static class DownedTargeting
    {
        private static bool _reported;

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(ThreatTargetActorService), "GetCurrentTargetActor",
                typeof(DownedTargeting), nameof(GetCurrentTargetActorPrefix));
        }

        /// <summary>Clears a knocked-out player as cached target, so the game's own chain picks again.</summary>
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

                // Logged once, since every attacker of a downed body fires this.
                if (!_reported)
                {
                    _reported = true;
                    MelonLogger.Msg("[Aggro] an enemy let go of a player who had gone down and chose again.");
                }
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside the AI tick, and a throw would lose the enemy's whole decision.
                MelonLogger.Error("[Aggro] letting go of a downed target failed: " + e);
            }
        }
    }
}
