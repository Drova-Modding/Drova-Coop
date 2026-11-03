using Il2CppDrova.AI.Logic;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>Harmony entry points of <see cref="DownedTargeting"/>.</summary>
    internal static class DownedTargetingPatches
    {
        internal static void GetCurrentTargetActorPrefix(ThreatTargetActorService __instance)
            => DownedTargeting.GetCurrentTargetActorPrefix(__instance);
    }
}
