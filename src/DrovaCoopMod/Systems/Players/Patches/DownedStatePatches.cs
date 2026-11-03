using Il2CppDrova;
using Il2CppDrova.ABI;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>Harmony entry points of <see cref="DownedState"/>.</summary>
    internal static class DownedStatePatches
    {
        internal static bool FirstPassPrefix(ReviveBlocker __instance, HealthChange healthChange, float dmgValue, ref float __result)
            => DownedState.FirstPassPrefix(__instance, healthChange, dmgValue, ref __result);
    }
}
