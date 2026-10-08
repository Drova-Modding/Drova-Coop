using Il2CppDrova.GUI;

namespace DrovaCoopMod.Systems.Loot.Patches
{
    /// <summary>The Harmony entry points of <see cref="Trade"/>. Each forwards to the system.</summary>
    internal static class TradePatches
    {
        internal static bool CanCompletePrefix(ref bool __result)
            => Trade.CanCompletePrefix(ref __result);

        internal static bool CompleteTradingPrefix(Il2CppTradingSystem.MarketPlace __instance, ref Il2CppTradingSystem.ResultState __result)
            => Trade.CompleteTradingPrefix(__instance, ref __result);

        internal static bool AcceptPrefix(GUI_Window_Merchant __instance)
            => Trade.AcceptPrefix(__instance);
    }
}
