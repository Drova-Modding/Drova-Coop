using MelonLoader;

namespace DrovaCoopMod
{
    /// <summary>
    /// Everything the player can set, held in MelonPreferences so a session can be started before there is
    /// any coop UI. The UI, when it exists, should write these same entries rather than a second copy.
    /// </summary>
    public static class CoopPreferences
    {
        private static MelonPreferences_Entry<string> _playerName = null!;
        private static MelonPreferences_Entry<string> _relayAddress = null!;
        private static MelonPreferences_Entry<int> _relayPort = null!;
        private static MelonPreferences_Entry<string> _sessionCode = null!;
        private static MelonPreferences_Entry<string> _sessionPassword = null!;

        /// <summary>The name shown to the other players.</summary>
        public static string PlayerName => _playerName.Value;

        /// <summary>Hostname or address of the relay to host on and join through.</summary>
        public static string RelayAddress => _relayAddress.Value;

        /// <summary>The relay's UDP port.</summary>
        public static int RelayPort => _relayPort.Value;

        /// <summary>
        /// The code to join, and for a host the code to claim. Empty as a host means the relay picks one,
        /// which is the better default.
        /// </summary>
        public static string SessionCode => _sessionCode.Value;

        /// <summary>
        /// The session password. Empty means an open session, which also means an unencrypted one, since
        /// the password is what the encryption key is derived from.
        /// </summary>
        public static string SessionPassword => _sessionPassword.Value;

        /// <summary>
        /// Creates the category and its entries. Called once at melon init.
        /// </summary>
        public static void Initialize()
        {
            MelonPreferences_Category category = MelonPreferences.CreateCategory("DrovaCoop");

            _playerName = category.CreateEntry("PlayerName", "Player", description: "Shown to the other players");
            _relayAddress = category.CreateEntry("RelayAddress", "127.0.0.1", description: "Relay hostname or address");
            _relayPort = category.CreateEntry("RelayPort", 9060, description: "Relay UDP port");
            _sessionCode = category.CreateEntry("SessionCode", string.Empty, description: "Code to join; empty when hosting lets the relay pick one");
            _sessionPassword = category.CreateEntry("SessionPassword", string.Empty, description: "Session password; also turns on end-to-end encryption");
        }

        /// <summary>
        /// The configured code, or null when it is blank. Null is what asks the relay for a code, so an
        /// empty setting and an absent one have to mean the same thing.
        /// </summary>
        public static string? SessionCodeOrNull()
        {
            return string.IsNullOrWhiteSpace(_sessionCode.Value) ? null : _sessionCode.Value.Trim();
        }

        /// <summary>
        /// The configured password, or null when it is blank.
        /// </summary>
        public static string? PasswordOrNull()
        {
            return string.IsNullOrEmpty(_sessionPassword.Value) ? null : _sessionPassword.Value;
        }
    }
}
