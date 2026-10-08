using Il2CppDrova;
using Il2CppDrova.AI.Modules;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>The Harmony entry points of <see cref="EnemyThreat"/>. Each forwards to the system.</summary>
    internal static class EnemyThreatPatches
    {
        internal static void OnDamageEventListenerPostfix(AIHealthAdapter __instance, HealthChangeArgs arg0)
            => EnemyThreat.OnDamageEventListenerPostfix(__instance, arg0);
    }
}
