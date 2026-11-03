using Il2CppDrova.HitSystem;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>Harmony entry points of <see cref="SpawnerShare"/>.</summary>
    internal static class SpawnerSharePatches
    {
        internal static bool EnterPrefix(HitReceiveBhvr_ActivateSpawner __instance)
            => SpawnerShare.EnterPrefix(__instance);
    }
}
