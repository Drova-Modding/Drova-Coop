using Il2CppDrova;
using Il2CppDrova.AI.Modules;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>Harmony entry points of <see cref="EnemyThreat"/>.</summary>
    internal static class EnemyThreatPatches
    {
        internal static void OnDamageEventListenerPostfix(AIHealthAdapter __instance, HealthChangeArgs arg0)
            => EnemyThreat.OnDamageEventListenerPostfix(__instance, arg0);
    }
}
