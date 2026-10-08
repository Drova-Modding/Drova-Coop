using Il2CppDrova;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="PausePolicy"/>. Each forwards to the system.</summary>
    internal static class PausePolicyPatches
    {
        internal static void CheckIsGamePausedPostfix(GameState_TimeScale __instance)
            => PausePolicy.CheckIsGamePausedPostfix(__instance);
    }
}
