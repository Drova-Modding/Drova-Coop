using Il2CppDrova.HitSystem;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="PuppetCombatState"/>. Each forwards to the system.</summary>
    internal static class PuppetCombatStatePatches
    {
        internal static bool EnterPrefix(HitReceiveBhvr_CombatBhvrs __instance, HitTriggerArgs args)
            => PuppetCombatState.EnterPrefix(__instance, args);
    }
}
