using Il2CppDrova.AI.Logic;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="DownedTargeting"/>. Each forwards to the system.</summary>
    internal static class DownedTargetingPatches
    {
        internal static void GetCurrentTargetActorPrefix(ThreatTargetActorService __instance)
            => DownedTargeting.GetCurrentTargetActorPrefix(__instance);
    }
}
