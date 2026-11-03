using Il2CppDrova;
using Il2CppDrova.Daytime;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>Harmony entry points of <see cref="ClockGuard"/>.</summary>
    internal static class ClockGuardPatches
    {
        internal static bool ClockWritePrefix(DaytimeGameHandler __instance, DrovaTime newDayTime, bool bySaving)
            => ClockGuard.ClockWritePrefix(__instance, newDayTime, bySaving);

        internal static void ScriptedPrefix()
            => ClockGuard.ScriptedPrefix();

        internal static void ScriptedPostfix()
            => ClockGuard.ScriptedPostfix();
    }
}
