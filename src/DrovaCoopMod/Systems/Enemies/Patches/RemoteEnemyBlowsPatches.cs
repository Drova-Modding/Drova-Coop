using Il2CppDrova.HitSystem;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>Harmony entry points of <see cref="RemoteEnemyBlows"/>.</summary>
    internal static class RemoteEnemyBlowsPatches
    {
        internal static void CheckFilterWithBhvrsPostfix(HitReceiveHandler __instance, HitTriggerArgs args, ref bool __result)
            => RemoteEnemyBlows.CheckFilterWithBhvrsPostfix(__instance, args, ref __result);
    }
}
