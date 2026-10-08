namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>The Harmony entry points of <see cref="HostWorldState"/>. Each forwards to the system.</summary>
    internal static class HostWorldStatePatches
    {
        internal static void IsActorAlivePostfix(string actorGuid, ref bool __result)
            => HostWorldState.IsActorAlivePostfix(actorGuid, ref __result);

        internal static void IsDeadPostfix(Il2CppDrova.Utilities.LazyLoading.LazyActor __instance, ref bool __result)
            => HostWorldState.IsDeadPostfix(__instance, ref __result);
    }
}
