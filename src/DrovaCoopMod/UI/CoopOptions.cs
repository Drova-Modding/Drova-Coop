using Drova_Modding_API.Access;
using Drova_Modding_API.UI.Builder;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2Cpp;
using Il2CppCustomFramework.Localization;
using MelonLoader;

namespace DrovaCoopMod.UI
{
    /// <summary>
    /// The coop section of the game's own options window, built through the modding API rather than drawn
    /// on top of it.
    ///
    /// Not everything the mod can be told fits here. The builder offers a switch, a slider, a dropdown, a
    /// button and a keybinding section - there is no text field, so the relay address, the session code
    /// and the password stay in MelonPreferences until there is one. What does fit is the part a player
    /// changes with an opinion rather than with a copy-paste: friendly fire, and the three buttons that
    /// start and end a session.
    ///
    /// Two stores, then, and they are kept apart on purpose rather than mirrored: what the option window
    /// shows lives in the game's own gameplay config, because that is where the row writes and reading it
    /// back from anywhere else would eventually disagree with what the player is looking at.
    /// </summary>
    internal static class CoopOptions
    {
        /// <summary>
        /// The mod's name to the localization system and the id the shared modding panel knows us by. One
        /// constant for both, because they are the same identity and two would drift.
        /// </summary>
        internal const string ModName = "DrovaCoop";

        /// <summary>
        /// The gameplay config key behind the friendly fire switch. Prefixed, because this key lands in the
        /// game's own config file next to the game's own settings.
        /// </summary>
        internal const string FriendlyFireKey = "DrovaCoop_FriendlyFire";

        /// <summary>
        /// The gameplay config key behind the recovery slider, as a percentage of maximum health.
        /// </summary>
        internal const string RecoveryHealthKey = "DrovaCoop_RecoveryHealth";

        private static bool _localizationWritten;

        /// <summary>
        /// Subscribes to the option window. Safe at melon init: this only attaches to an event, and
        /// everything that reaches for the game happens when it fires.
        /// </summary>
        internal static void Initialize()
        {
            OptionMenuAccess.Instance.OnOptionMenuOpen += Build;
        }

        /// <summary>
        /// Builds the rows, every time the window opens.
        ///
        /// Every time, because the API clears what mods added when the window closes - the panel is a
        /// clone of the game's own, and it does not survive. <c>GetBuilder</c> returns null when this mod
        /// has already been added to the panel that is currently open, which is what keeps a second call
        /// in the same open from producing a second set of rows.
        /// </summary>
        private static void Build()
        {
            try
            {
                WriteLocalizationOnce();

                OptionUIBuilder? builder = OptionMenuAccess.Instance.GetBuilder(ModName);
                if (builder == null) return;

                builder
                    .CreateHeader(Text("Header"))
                    .CreateSwitch(Text("FriendlyFire"), Text("On"), Text("Off"), FriendlyFireKey, defaultValue: false)
                    .CreateDisclaimer(Text("FriendlyFireHint"))
                    .CreateSlider(Text("RecoveryHealth"), RecoveryHealthKey, min: 1, max: 100,
                        defaultValue: CoopProtocol.DefaultRecoveryHealthPercent)
                    .CreateDisclaimer(Text("RecoveryHealthHint"))
                    .CreateButton(Text("Host"), Text("HostAction"), OnHost)
                    .CreateButton(Text("Join"), Text("JoinAction"), OnJoin)
                    .CreateButton(Text("Leave"), Text("LeaveAction"), OnLeave)
                    .Build();
            }
            catch (Exception e)
            {
                // The window is the game's, and this runs inside its open. Letting an exception out of here
                // leaves the player looking at a half-built options screen with no idea why.
                MelonLogger.Error("[Coop] Failed to build the options panel: " + e);
            }
        }

        private static void OnHost()
        {
            Run(CoopActions.Host);
        }

        private static void OnJoin()
        {
            // No argument: a button cannot carry one. The configured code is the only thing it can mean,
            // and CoopActions says so plainly when there is none.
            Run(() => CoopActions.Join());
        }

        private static void OnLeave()
        {
            Run(CoopActions.Leave);
        }

        /// <summary>
        /// Runs a button's action and logs what it reported. Buttons are clicked from a Unity event, which
        /// swallows what an interop callback throws, so anything not caught here is lost.
        /// </summary>
        private static void Run(Func<string> action)
        {
            try
            {
                MelonLogger.Msg("[Coop] " + action());
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Coop] Options button failed: " + e);
            }
        }

        private static LocalizedString Text(string key)
        {
            return LocalizationAccess.GetLocalizedString(ModName, key);
        }

        /// <summary>
        /// Writes the mod's strings into the game's localization folder and reloads the current language.
        ///
        /// Deferred to the first time the window opens rather than done at melon init, because it needs the
        /// localization database to exist to know where its folders are. Once per run: the files do not
        /// change while the game is running, and the reload is not free.
        ///
        /// English and German only. An unlisted language falls back to what the game does with a missing
        /// key, which shows the key - ugly, but it names exactly what is missing, and inventing machine
        /// translations of a setting that can get someone killed is worse.
        /// </summary>
        private static void WriteLocalizationOnce()
        {
            if (_localizationWritten) return;
            _localizationWritten = true;

            List<LocalizationAccess.LocalizationEntry> entries =
            [
                new("Header", "Coop", LocalizationDB.ELanguage.en),
                new("FriendlyFire", "Friendly fire", LocalizationDB.ELanguage.en),
                new("FriendlyFireHint", "Players can hurt each other. Only the host's setting counts; it applies at once, mid-session too.", LocalizationDB.ELanguage.en),
                new("On", "On", LocalizationDB.ELanguage.en),
                new("Off", "Off", LocalizationDB.ELanguage.en),
                new("RecoveryHealth", "Health after getting up", LocalizationDB.ELanguage.en),
                new("RecoveryHealthHint", "Percent of maximum a downed player stands up with. Only the host's setting counts.", LocalizationDB.ELanguage.en),
                new("Host", "Host a session", LocalizationDB.ELanguage.en),
                new("HostAction", "Host", LocalizationDB.ELanguage.en),
                new("Join", "Join a session", LocalizationDB.ELanguage.en),
                new("JoinAction", "Join", LocalizationDB.ELanguage.en),
                new("Leave", "Leave the session", LocalizationDB.ELanguage.en),
                new("LeaveAction", "Leave", LocalizationDB.ELanguage.en),

                new("Header", "Koop", LocalizationDB.ELanguage.de),
                new("FriendlyFire", "Freundliches Feuer", LocalizationDB.ELanguage.de),
                new("FriendlyFireHint", "Spieler können sich gegenseitig verletzen. Es zählt nur die Einstellung des Hosts; sie greift sofort, auch mitten in der Sitzung.", LocalizationDB.ELanguage.de),
                new("On", "An", LocalizationDB.ELanguage.de),
                new("Off", "Aus", LocalizationDB.ELanguage.de),
                new("RecoveryHealth", "Leben nach dem Aufstehen", LocalizationDB.ELanguage.de),
                new("RecoveryHealthHint", "Prozent des Maximums, mit dem ein Gestürzter aufsteht. Es zählt nur die Einstellung des Hosts.", LocalizationDB.ELanguage.de),
                new("Host", "Sitzung hosten", LocalizationDB.ELanguage.de),
                new("HostAction", "Hosten", LocalizationDB.ELanguage.de),
                new("Join", "Sitzung beitreten", LocalizationDB.ELanguage.de),
                new("JoinAction", "Beitreten", LocalizationDB.ELanguage.de),
                new("Leave", "Sitzung verlassen", LocalizationDB.ELanguage.de),
                new("LeaveAction", "Verlassen", LocalizationDB.ELanguage.de),
            ];

            LocalizationAccess.CreateLocalizationEntries(entries, ModName);
        }
    }
}
