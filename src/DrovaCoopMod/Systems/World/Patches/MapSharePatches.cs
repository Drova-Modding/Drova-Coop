using Il2CppDrova.MapSystem;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="MapShare"/>. Each forwards to the system.</summary>
    internal static class MapSharePatches
    {
        internal static void SetMapEnabledPostfix(MapData __instance, bool enabled, bool bySavingLoading)
            => MapShare.SetMapEnabledPostfix(__instance, enabled, bySavingLoading);
    }
}
