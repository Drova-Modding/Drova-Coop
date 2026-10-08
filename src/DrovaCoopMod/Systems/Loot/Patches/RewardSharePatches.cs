namespace DrovaCoopMod.Systems.Loot.Patches
{
    /// <summary>The Harmony entry points of <see cref="RewardShare"/>. Each forwards to the system.</summary>
    internal static class RewardSharePatches
    {
        internal static void GiveExpPrefix(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp __instance, out int __state)
            => RewardShare.GiveExpPrefix(__instance, out __state);

        internal static void GiveExpPostfix(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp __instance, int __state)
            => RewardShare.GiveExpPostfix(__instance, __state);
    }
}
