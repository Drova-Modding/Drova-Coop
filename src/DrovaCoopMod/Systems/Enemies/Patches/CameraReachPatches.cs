using Il2CppDrova.AI.Modules;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>The Harmony entry points of <see cref="CameraReach"/>. Each forwards to the system.</summary>
    internal static class CameraReachPatches
    {
        internal static void IsChangeToActorPossiblePostfix(AI_ActorFactory __instance, ref bool __result)
            => CameraReach.IsChangeToActorPossiblePostfix(__instance, ref __result);

        internal static bool ChangeToRefPrefix(AI_ActorFactory __instance)
            => CameraReach.ChangeToRefPrefix(__instance);
    }
}
