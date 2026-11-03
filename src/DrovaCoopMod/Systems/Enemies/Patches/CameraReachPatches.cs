using Il2CppDrova.AI.Modules;
using Il2CppDrova.HitSystem;
using Il2CppDrova.Utilities.LazyLoading;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>Harmony entry points of <see cref="CameraReach"/>.</summary>
    internal static class CameraReachPatches
    {
        internal static void IsChangeToActorPossiblePostfix(AI_ActorFactory __instance, ref bool __result)
            => CameraReach.IsChangeToActorPossiblePostfix(__instance, ref __result);

        internal static bool ChangeToRefPrefix(AI_ActorFactory __instance)
            => CameraReach.ChangeToRefPrefix(__instance);

        internal static void TriggerLoadPriorityPostfix(HitTriggerHandler __instance, ref LazyPriority __result)
            => CameraReach.TriggerLoadPriorityPostfix(__instance, ref __result);

        internal static void ReceiverLoadPriorityPostfix(HitReceiveHandler __instance, ref LazyPriority __result)
            => CameraReach.ReceiverLoadPriorityPostfix(__instance, ref __result);
    }
}
