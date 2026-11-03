using Il2CppDrova;
using Il2CppDrova.Saveables;

namespace DrovaCoopMod.Systems.Session.Patches
{
    /// <summary>Harmony entry points of <see cref="HostSettings"/>.</summary>
    internal static class HostSettingsPatches
    {
        internal static void GetGameModePostfix(SavegameData data, ref GameMode __result)
            => __result = HostSettings.FollowHostMode(__result);
    }
}
