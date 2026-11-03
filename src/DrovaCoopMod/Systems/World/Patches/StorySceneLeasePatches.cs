using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>Harmony entry points of <see cref="StorySceneLease"/>.</summary>
    internal static class StorySceneLeasePatches
    {
        internal static bool UpdatePrefix(GlobalObserverBhvr_StartDialogue __instance)
            => StorySceneLease.UpdatePrefix(__instance);

        internal static bool IntentPrefix(ActorIntentBhvr __instance)
            => StorySceneLease.IntentPrefix(__instance);

        internal static bool WavesPrefix(GlobalObserverBhvr_WavesSpawning __instance)
            => StorySceneLease.WavesPrefix(__instance);
    }
}
