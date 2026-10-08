using Il2CppNodeCanvas.DialogueTrees.UI;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="DialogueWindowGuard"/>. Each forwards to the system.</summary>
    internal static class DialogueWindowGuardPatches
    {
        internal static void PrunePrefix(DS_DialogueWindowManager __instance)
            => DialogueWindowGuard.PrunePrefix(__instance);
    }
}
