using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.InteractionSystem;

namespace DrovaCoopMod.Systems.Loot.Patches
{
    /// <summary>The Harmony entry points of <see cref="HaulShare"/>. Each forwards to the system.</summary>
    internal static class HaulSharePatches
    {
        internal static void SpawnPostfix(Interact_Bhvr_Spot_Rubble __instance, int spotsLeft)
            => HaulShare.SpawnPostfix(__instance, spotsLeft);

        internal static void LootPrefix(Interact_Bhvr_LootAll __instance)
            => HaulShare.LootPrefix(__instance);

        internal static void LootPostfix(Actor actor)
            => HaulShare.LootPostfix(actor);

        internal static bool OperatePrefix(GInt __instance, GInt.Operator op, int value)
            => HaulShare.OperatePrefix(__instance, op, value);
    }
}
