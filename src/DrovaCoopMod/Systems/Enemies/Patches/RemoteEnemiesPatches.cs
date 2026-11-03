using Il2CppDrova.AI.Logic;
using Il2CppDrova.AI.Modules;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>Harmony entry points of <see cref="RemoteEnemies"/>.</summary>
    internal static class RemoteEnemiesPatches
    {
        internal static void TargetPostfix(AI_TargetBhvr __instance, ref IAIActor? __result)
            => RemoteEnemy.AimAtHostTarget(__instance._owner, ref __result);

        internal static void EntityTargetPostfix(AI_EntityTargetBhvr __instance, ref IAIActor? __result)
            => RemoteEnemy.AimAtHostTarget(__instance._owner, ref __result);
    }
}
