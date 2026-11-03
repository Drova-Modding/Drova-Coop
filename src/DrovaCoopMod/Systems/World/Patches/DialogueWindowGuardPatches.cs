using Il2CppNodeCanvas.DialogueTrees.UI;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>Harmony entry points of <see cref="DialogueWindowGuard"/>.</summary>
    internal static class DialogueWindowGuardPatches
    {
        internal static void PrunePrefix(DS_DialogueWindowManager __instance)
            => DialogueWindowGuard.PrunePrefix(__instance);
    }
}
