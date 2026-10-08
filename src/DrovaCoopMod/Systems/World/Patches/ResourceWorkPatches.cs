using Il2CppDrova.InteractionSystem;
using Il2CppDrova.Seaveables;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="ResourceWork"/>. Each forwards to the system.</summary>
    internal static class ResourceWorkPatches
    {
        internal static void MinigamePrefix(Interact_Bhvr_ResourceSpot __instance, out int __state)
            => ResourceWork.MinigamePrefix(__instance, out __state);

        internal static void MinigamePostfix(Interact_Bhvr_ResourceSpot __instance, int __state)
            => ResourceWork.MinigamePostfix(__instance, __state);

        internal static void RegisterPostfix(Saveable_ResourceSpot __instance)
            => ResourceWork.RegisterPostfix(__instance);

        internal static void AwakePostfix(Interact_Bhvr_ResourceSpot __instance)
            => ResourceWork.AwakePostfix(__instance);
    }
}
