using Il2CppDrova.HitSystem;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="GuardReach"/>. Each forwards to the system.</summary>
    internal static class GuardReachPatches
    {
        internal static void ManagedUpdatePostfix(HitReceiveHandler __instance)
            => GuardReach.ManagedUpdatePostfix(__instance);

        internal static void LoadPriorityPostfix(HitReceiveHandler __instance, ref Il2CppDrova.Utilities.LazyLoading.LazyPriority __result)
            => GuardReach.LoadPriorityPostfix(__instance, ref __result);
    }
}
