using Il2CppDrova.Weather.Fog;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="FogletShare"/>. Each forwards to the system.</summary>
    internal static class FogletSharePatches
    {
        internal static void ActivatedPostfix(FogletFogBhvr __instance)
            => FogletShare.ActivatedPostfix(__instance);

        internal static bool EnterPrefix(FogletFogBhvr __instance)
            => FogletShare.EnterPrefix(__instance);

        internal static bool ExitPrefix(FogletFogBhvr __instance)
            => FogletShare.ExitPrefix(__instance);
    }
}
