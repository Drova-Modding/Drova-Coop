using Il2CppDrova;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="DamageWatch"/>. Each forwards to the system.</summary>
    internal static class DamageWatchPatches
    {
        internal static void CheckEventsPostfix(Health __instance, ref HealthChangeArgs changeArgs)
            => DamageWatch.CheckEventsPostfix(__instance, ref changeArgs);

        internal static void CheckEventsPrefix(Health __instance)
            => DamageWatch.CheckEventsPrefix(__instance);
    }
}
