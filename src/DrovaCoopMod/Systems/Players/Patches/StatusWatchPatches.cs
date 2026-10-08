using Il2CppDrova.StatusEffects;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="StatusWatch"/>. Each forwards to the system.</summary>
    internal static class StatusWatchPatches
    {
        internal static void AddEffectPostfix(StatusEffectHandler __instance, StatusEffect effect, SpawnInfo info)
            => StatusWatch.AddEffectPostfix(__instance, effect, info);

        internal static bool CopyEffectOnLocalPrefix(StatusEffectHandler __instance, SpawnInfo info)
            => StatusWatch.CopyEffectOnLocalPrefix(__instance, info);

        internal static void RemoveEffectsPostfix(StatusEffectHandler __instance, Il2CppSystem.Collections.Generic.List<StatusEffect> prefabEffects)
            => StatusWatch.RemoveEffectsPostfix(__instance, prefabEffects);
    }
}
