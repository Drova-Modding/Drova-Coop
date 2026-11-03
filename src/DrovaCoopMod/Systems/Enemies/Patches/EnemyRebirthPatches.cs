using Il2CppDrova.Saveables;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>Harmony entry points of <see cref="EnemyRebirth"/>.</summary>
    internal static class EnemyRebirthPatches
    {
        internal static bool LoadCallbackPrefix(Saveable_Health __instance)
            => EnemyRebirth.LoadCallbackPrefix(__instance);
    }
}
