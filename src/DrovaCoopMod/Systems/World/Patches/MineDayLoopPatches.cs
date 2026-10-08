using Il2CppDrova.InteractionSystem;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="MineDayLoop"/>. Each forwards to the system.</summary>
    internal static class MineDayLoopPatches
    {
        internal static void ReseatPrefix(Interact_Special_MineDayLoop __instance)
            => MineDayLoop.ReseatPrefix(__instance);
    }
}
