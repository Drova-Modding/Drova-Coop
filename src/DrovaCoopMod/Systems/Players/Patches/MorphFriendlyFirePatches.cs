using Il2CppDrova.HitSystem;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>Harmony entry point of <see cref="MorphFriendlyFire"/>.</summary>
    internal static class MorphFriendlyFirePatches
    {
        internal static bool CheckPrefix(HitFilter_Alignment __instance, HitTriggerArgs triggerArgs, ref bool __result)
            => MorphFriendlyFire.CheckPrefix(__instance, triggerArgs, ref __result);
    }
}
