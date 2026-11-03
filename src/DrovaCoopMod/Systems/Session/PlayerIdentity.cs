using Drova_Modding_API.Access;
using Il2CppDrova.GlobalVarSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>The local player's character-creation name, so the session shows the same name the game does.</summary>
    internal static class PlayerIdentity
    {
        /// <summary>The player's GVar list, by the guid UserAnalyticsReport.SendReport reads.</summary>
        private const string PlayerVarListGuid = "40ea2cd94a6720a4a8b9e4200debd1a7";

        private const string NameVar = "Name";

        private static bool _warned;

        /// <summary>The character name, or null while the game cannot answer. Not cached, since a save load changes it.</summary>
        internal static string? LocalName()
        {
            try
            {
                SubDatabase_GVars? database = ProviderAccess.GVarDatabase;
                if (database == null) return null;

                GVarList? list = database.GetGVarListByGuid(PlayerVarListGuid);
                if (list == null) return Warn("no player variable list; the game may still be loading");

                AGVarBase? variable = list.GetGVarByName(NameVar);
                GString? name = variable == null ? null : variable.TryCast<GString>();
                if (name == null) return Warn($"the player variable list has no {NameVar} string");

                string value = name.GetValue();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception e)
            {
                return Warn(e.Message);
            }
        }

        /// <summary>Warns once, since this runs on every host start and join.</summary>
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
