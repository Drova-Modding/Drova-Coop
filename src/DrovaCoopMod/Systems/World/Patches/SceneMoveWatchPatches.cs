using Il2CppDrova.Cutscenes;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="SceneMoveWatch"/>. Each forwards to the system.</summary>
    internal static class SceneMoveWatchPatches
    {
        internal static void InteractPostfix(Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode __instance)
            => SceneMoveWatch.InteractPostfix(__instance);

        internal static void StartActionPostfix(CS_Action_Move __instance)
            => SceneMoveWatch.StartActionPostfix(__instance);
    }
}
