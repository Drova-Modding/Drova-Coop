using Il2CppDrova;
using Il2CppDrova.ABI;
using Il2CppDrova.ABI.Damage;
using Il2CppDrova.Items;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="ItemUseWatch"/>. Each forwards to the system.</summary>
    internal static class ItemUseWatchPatches
    {
        internal static void StartUseItemPrefix(Item __instance, Actor actor)
            => ItemUseWatch.StartUseItemPrefix(__instance, actor);

        internal static void StartUseItemPostfix()
            => ItemUseWatch.StartUseItemPostfix();

        internal static void EndUseItemPostfix(Item __instance, Actor actor)
            => ItemUseWatch.EndUseItemPostfix(__instance, actor);

        internal static bool HitEffectDamagePrefix(ABI_Bhvr_HitEffect_Damage __instance, Entity entity)
            => ItemUseWatch.HitEffectDamagePrefix(__instance, entity);

        internal static bool SelfTickPrefix(ABI_Bhvr_Damage_Self __instance)
            => ItemUseWatch.SelfTickPrefix(__instance);

        internal static bool HandlePlayerTargetPosPrefix(Entity owner)
            => ItemUseWatch.HandlePlayerTargetPosPrefix(owner);
    }
}
