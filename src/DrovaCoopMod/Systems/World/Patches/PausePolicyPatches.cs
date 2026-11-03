using Il2CppDrova;
using Il2CppDrova.GUI;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>Harmony entry points of <see cref="PausePolicy"/>.</summary>
    internal static class PausePolicyPatches
    {
        internal static void CheckIsGamePausedPostfix(GameState_TimeScale __instance)
            => PausePolicy.CheckIsGamePausedPostfix(__instance);

        internal static bool SliderGameUpdatePrefix(GUI_SliderGame __instance)
            => PausePolicy.SliderGameUpdatePrefix(__instance);
    }
}
