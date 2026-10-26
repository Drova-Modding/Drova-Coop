using Il2CppDrova.Poise;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="PuppetPoise"/>. Each forwards to the system.</summary>
    internal static class PuppetPoisePatches
    {
        internal static bool ChangePoisePrefix(PoiseController __instance, float delta, bool force)
            => PuppetPoise.ChangePoisePrefix(__instance, delta, force);

        internal static bool ChangePoiseUnforcedPrefix(PoiseController __instance, float delta)
            => PuppetPoise.ChangePoisePrefix(__instance, delta, false);
    }
}
