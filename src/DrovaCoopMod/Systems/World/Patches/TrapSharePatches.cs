using Il2CppDrova;
using Il2CppDrova.Traps;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="TrapShare"/>. Each forwards to the system.</summary>
    internal static class TrapSharePatches
    {
        internal static bool InitSetupPrefix(TrapController __instance, bool isFromPlayer, Actor actor)
            => TrapShare.InitSetupPrefix(__instance, isFromPlayer, actor);
    }
}
