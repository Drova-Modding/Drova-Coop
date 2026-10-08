using Il2CppDrova.AI.Graphs;
using Il2CppDrova.AI.Modules;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>The Harmony entry points of <see cref="EnemyPerception"/>. Each forwards to the system.</summary>
    internal static class EnemyPerceptionPatches
    {
        internal static void IsPlayerInRangePostfix(ManualGraphUpdater __instance, ref bool __result)
            => EnemyPerception.IsPlayerInRangePostfix(__instance, ref __result);

        internal static void OnCheckPostfix(IsPlayerInRangeCondition __instance, ref bool __result)
            => EnemyPerception.OnCheckPostfix(__instance, ref __result);
    }
}
