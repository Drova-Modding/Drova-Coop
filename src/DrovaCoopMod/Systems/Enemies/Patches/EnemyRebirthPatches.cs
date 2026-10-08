using Il2CppDrova.Saveables;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>The Harmony entry points of <see cref="EnemyRebirth"/>. Each forwards to the system.</summary>
    internal static class EnemyRebirthPatches
    {
        internal static bool LoadCallbackPrefix(Saveable_Health __instance)
            => EnemyRebirth.LoadCallbackPrefix(__instance);
    }
}
