using Drova_Modding_API.Access;
using Il2CppDrova.GlobalVarSystem;
using MelonLoader;

namespace DrovaCoopMod.Session
{
    /// <summary>
    /// Who the local player is, as the game already knows them.
    ///
    /// The name is the one typed at character creation, not a coop-specific setting. A second name would
    /// be a second identity: the other player would see "Flo" over a body the save file, the load screen
    /// and every piece of dialogue call something else. Asking twice for something already answered is
    /// also the kind of setting people leave on its default, which is how sessions end up with two
    /// players both called "Player".
    /// </summary>
    internal static class PlayerIdentity
    {
        /// <summary>
        /// The GVar list holding the player's own variables, addressed by guid rather than by name because
        /// that is how the game addresses it - <c>UserAnalyticsReport.SendReport</c> reads the same name
        /// from the same guid. A guid survives a rename; a display name does not.
        /// </summary>
        private const string PlayerVarListGuid = "40ea2cd94a6720a4a8b9e4200debd1a7";

        /// <summary>The variable inside that list, a <see cref="GString"/>.</summary>
        private const string NameVar = "Name";

        private static bool _warned;

        /// <summary>
        /// The player's character name, or null when the game cannot answer yet.
        ///
        /// Null rather than a placeholder, because the callers already have one: the session names an
        /// unnamed player after their id, which at least stays unique. Read fresh each time rather than
        /// cached - loading a different save changes the answer, and the value is wanted twice per
        /// session.
        /// </summary>
        internal static string? LocalName()
        {
            try
            {
                SubDatabase_GVars database = ProviderAccess.GVarDatabase;
                if (database == null) return null;

                GVarList list = database.GetGVarListByGuid(PlayerVarListGuid);
                if (list == null) return Warn("no player variable list; the game may still be loading");

                // TryCast, not a C# cast: the interop type is decided by the native object, and a plain
                // cast on a mismatch throws out of IL2CPP rather than returning null.
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

        /// <summary>
        /// Says why the name is unknown, once. Called from a path that runs on every host start and every
        /// join, so an unconditional warning would repeat for as long as the cause lasts.
        /// </summary>
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
