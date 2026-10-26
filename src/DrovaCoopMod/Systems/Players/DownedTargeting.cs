using Drova_Modding_API;
using DrovaCoopMod.Systems.Players.Patches;
using Il2CppDrova;
using Il2CppDrova.AI.Logic;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Makes an enemy pick a new target when the player body it was fighting goes down.
    /// GetCurrentTargetActor keeps its cache while the actor exists, so a knocked-out player stays targeted otherwise.
    /// </summary>
    internal sealed class DownedTargeting : ICoopSystem
    {
        private static bool _reported;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(ThreatTargetActorService), "GetCurrentTargetActor",
                typeof(DownedTargetingPatches), nameof(DownedTargetingPatches.GetCurrentTargetActorPrefix));
        }

        /// <summary>Clears a knocked-out player as cached target, so the game's own chain picks again.</summary>
        internal static void GetCurrentTargetActorPrefix(ThreatTargetActorService __instance)
        {
            try
            {
                IAIActor? cached =__instance._currentCachedTarget;
                if (cached == null) return;

                // Every target query passes here, so anything but a player body leaves before the cast.
                Il2CppObjectBase? cachedObject = cached.GetObject();
                if (cachedObject == null || !RemotePlayers.IsPlayersBody(cachedObject)) return;

                Actor? target = cachedObject.TryCast<Actor>();
                if (target == null) return;

                if (target.CustomStats == null || !target.CustomStats.IsKnockout) return;

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
