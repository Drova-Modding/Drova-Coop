using Il2CppDrova.Cutscenes;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>Harmony entry points of <see cref="CutscenePorts"/>.</summary>
    internal static class CutscenePortsPatches
    {
        internal static void TeleportPostfix(CS_Action_TeleportActor __instance)
            => CutscenePorts.TeleportPostfix(__instance);

        internal static void RoutinePostfix(CS_Action_PortActorToRoutines __instance)
            => CutscenePorts.RoutinePostfix(__instance);
    }
}
