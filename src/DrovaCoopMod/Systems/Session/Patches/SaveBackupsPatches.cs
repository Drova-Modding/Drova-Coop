using Il2CppDrova;
using Il2CppDrova.Saveables;

namespace DrovaCoopMod.Systems.Session.Patches
{
    /// <summary>The Harmony entry points of <see cref="SaveBackups"/>. Each forwards to the system.</summary>
    internal static class SaveBackupsPatches
    {
        internal static void SavePrefix(SavegameGameHandler __instance, SavegameIdentifier id, SaveOptions saveOptions, bool ignoreRestriction)
            => SaveBackups.SavePrefix(__instance, id, saveOptions, ignoreRestriction);
    }
}
