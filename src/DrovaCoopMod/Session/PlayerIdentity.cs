using Drova_Modding_API.Access;
using Il2CppDrova.GlobalVarSystem;
using MelonLoader;

namespace DrovaCoopMod.Session
{
    /// <summary>
    /// The local player's character-creation name, so the session shows the same name the game does.
    /// </summary>
    internal static class PlayerIdentity
    {
        // The player's GVar list, by the same guid UserAnalyticsReport.SendReport reads.
        private const string PlayerVarListGuid = "40ea2cd94a6720a4a8b9e4200debd1a7";

        private const string NameVar = "Name";

        private static bool _warned;

        /// <summary>
        /// The player's character name, or null when the game cannot answer yet. Not cached, since a save load changes it.
        /// </summary>
        internal static string? LocalName()
        {
            try
            {
                SubDatabase_GVars database = ProviderAccess.GVarDatabase;
                if (database == null) return null;

                GVarList list = database.GetGVarListByGuid(PlayerVarListGuid);
                if (list == null) return Warn("no player variable list; the game may still be loading");

                GString? name = list.GetGVarByName(NameVar)?.TryCast<GString>();
                if (name == null) return Warn($"the player variable list has no {NameVar} string");

                string value = name.GetValue();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception e)
            {
                return Warn(e.Message);
            }
        }

        // Warns once, since this runs on every host start and join.
        private static string? Warn(string reason)
        {
            if (!_warned)
            {
                _warned = true;
                MelonLogger.Warning($"[Coop] Cannot read the character name ({reason}); players will be shown by id.");
            }

            return null;
        }
    }
}
