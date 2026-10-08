using Il2CppDrova.Cutscenes;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="CutsceneWatch"/>. Each forwards to the system.</summary>
    internal static class CutsceneWatchPatches
    {
        internal static void OnExecutePostfix(CS_CutsceneActionNode __instance)
            => CutsceneWatch.OnExecutePostfix(__instance);
    }
}
