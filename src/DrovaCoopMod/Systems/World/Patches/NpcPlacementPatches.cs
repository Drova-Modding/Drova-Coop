namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>Harmony entry points of <see cref="NpcPlacement"/>.</summary>
    internal static class NpcPlacementPatches
    {
        internal static void RoutinePlacePostfix(Il2CppDrova.Routine.ActorRoutineModule __instance, ref Il2CppDrova.Routine.IRoutinePlace? __result)
            => NpcPlacement.RoutinePlacePostfix(__instance, ref __result);

        internal static void UnloadPrefix(Il2CppDrova.Utilities.LazyLoading.LazyActor __instance)
            => NpcPlacement.UnloadPrefix(__instance);
    }
}
