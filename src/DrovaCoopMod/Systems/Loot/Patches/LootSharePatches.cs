using Il2CppDrova;
using Il2CppDrova.InteractionSystem;
using Il2CppDrova.InventorySystem;
using Il2CppDrova.Saveables;

namespace DrovaCoopMod.Systems.Loot.Patches
{
    /// <summary>The Harmony entry points of <see cref="LootShare"/>. Each forwards to the system.</summary>
    internal static class LootSharePatches
    {
        internal static void PickupStartPostfix(Saveable_PickUp_Once __instance)
            => LootShare.PickupStartPostfix(__instance);

        internal static void LootAllPostfix(Interact_Bhvr_LootAll __instance, Actor actor)
            => LootShare.LootAllPostfix(__instance, actor);

        internal static void ContainerStartPostfix(Interact_Bhvr_LootInventory __instance)
            => LootShare.ContainerStartPostfix(__instance);

        internal static void InventoryChangedPostfix(Interact_Bhvr_LootInventory __instance, InventoryChangedEventArgs arg0, bool __state)
            => LootShare.InventoryChangedPostfix(__instance, arg0, __state);

        internal static void InventoryChangedPrefix(Interact_Bhvr_LootInventory __instance, out bool __state)
            => LootShare.InventoryChangedPrefix(__instance, out __state);

        internal static void CreateLootInventoryPrefix(Interact_Bhvr_LootInventory __instance)
            => LootShare.CreateLootInventoryPrefix(__instance);

        internal static void TransferPostfix(Inventory from, Inventory to)
            => LootShare.TransferPostfix(from, to);
    }
}
