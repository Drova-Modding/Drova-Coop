using Il2CppDrova.ActorActions;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>Harmony entry points of <see cref="BlockVerdicts"/>.</summary>
    internal static class BlockVerdictsPatches
    {
        internal static bool CalculateResultDamagePrefix(AA_BlockAction __instance, ref float __result)
            => BlockVerdicts.CalculateResultDamagePrefix(__instance, ref __result);
    }
}
