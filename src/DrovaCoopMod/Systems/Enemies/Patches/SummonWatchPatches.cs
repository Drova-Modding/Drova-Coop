using Il2CppDrova.ABI;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>Harmony entry points of <see cref="SummonWatch"/>.</summary>
    internal static class SummonWatchPatches
    {
        internal static void OnAbilityInitPostfix(ABI_Bhvr_Conjure __instance)
            => SummonWatch.OnAbilityInitPostfix(__instance);
    }
}
