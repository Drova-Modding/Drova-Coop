using Il2CppDrova.ActorActions;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="BlockVerdicts"/>. Each forwards to the system.</summary>
    internal static class BlockVerdictsPatches
    {
        internal static bool CalculateResultDamagePrefix(AA_BlockAction __instance, ref float __result)
            => BlockVerdicts.CalculateResultDamagePrefix(__instance, ref __result);
    }
}
