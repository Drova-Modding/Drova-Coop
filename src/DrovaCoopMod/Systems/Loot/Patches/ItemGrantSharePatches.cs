using Il2CppDrova.DialogueNew;
using Il2CppNodeCanvas.DialogueTrees;

namespace DrovaCoopMod.Systems.Loot.Patches
{
    /// <summary>The Harmony entry points of <see cref="ItemGrantShare"/>. Each forwards to the system.</summary>
    internal static class ItemGrantSharePatches
    {
        internal static void GiveItemPrefix(DS_GiveItemNode __instance, out List<DialogItemsExchange>? __state)
            => ItemGrantShare.GiveItemPrefix(__instance, out __state);

        internal static void GiveItemPostfix(DS_GiveItemNode __instance, List<DialogItemsExchange>? __state)
            => ItemGrantShare.GiveItemPostfix(__instance, __state);
    }
}
