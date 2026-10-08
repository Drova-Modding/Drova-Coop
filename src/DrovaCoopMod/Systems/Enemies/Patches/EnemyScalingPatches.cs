using Il2CppDrova;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>The Harmony entry points of <see cref="EnemyScaling"/>. Each forwards to the system.</summary>
    internal static class EnemyScalingPatches
    {
        internal static void CalculatePostfix(HealthChange __instance, Health defenderHealth, ref HealthChange.HealthChangeCalc __result)
            => EnemyScaling.CalculatePostfix(__instance, defenderHealth, ref __result);
    }
}
