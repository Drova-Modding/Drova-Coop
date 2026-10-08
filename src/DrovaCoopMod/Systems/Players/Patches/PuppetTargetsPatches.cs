using Il2CppDrova.AI.Modules;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="PuppetTargets"/>. Each forwards to the system.</summary>
    internal static class PuppetTargetsPatches
    {
        internal static void IsPlayerPostfix(AIActorAdapter __instance, ref bool __result)
            => PuppetTargets.IsPlayerPostfix(__instance, ref __result);
    }
}
