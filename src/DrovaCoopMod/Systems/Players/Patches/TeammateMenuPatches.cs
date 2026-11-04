namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>Harmony entry points of <see cref="TeammateMenu"/>.</summary>
    internal static class TeammateMenuPatches
    {
        internal static bool LocalizePrefix(string locaPath, string locaKey, ref string __result)
            => TeammateMenu.LocalizePrefix(locaPath, locaKey, ref __result);

        internal static void CanTalkPostfix(Il2CppDrova.InteractionSystem.Interact_Condition_IsInCombat __instance, ref bool __result)
            => TeammateMenu.CanTalkPostfix(__instance, ref __result);
    }
}
