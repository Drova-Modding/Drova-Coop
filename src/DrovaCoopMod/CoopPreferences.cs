using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.UI;
using MelonLoader;

namespace DrovaCoopMod
{
    /// <summary>
    /// Everything the player can set.
    ///
    /// Split across two stores, and not by accident. What the options window can show lives in the game's
    /// own gameplay config, because that is where the option row writes - reading it from anywhere else
    /// would eventually disagree with what the player is looking at. Everything the window cannot show
    /// stays in MelonPreferences: the builder in <see cref="CoopOptions"/> has no text field, so an
    /// address, a session code and a password have nowhere else to go yet.
    ///
    /// The player's name is in neither. It comes from the character, through
    /// <see cref="Session.PlayerIdentity"/>.
    /// </summary>
    public static class CoopPreferences
    {
        private static MelonPreferences_Entry<string> _relayAddress = null!;
        private static MelonPreferences_Entry<int> _relayPort = null!;
        private static MelonPreferences_Entry<string> _sessionCode = null!;
        private static MelonPreferences_Entry<string> _sessionPassword = null!;

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
        /// Whether players may damage each other, as set by the switch in the options window.
        ///
        /// **Only the host's copy of this decides.** It is sent with the handshake and clients adopt what
        /// they are told, because a setting each machine read for itself would let one player be
        /// vulnerable to a friend who was not vulnerable back.
        ///
        /// Absent until the options window has been opened once - the switch is what writes the key - and
        /// absent reads as off, which is the same answer the switch's own default gives.
        /// </summary>
        public static bool FriendlyFire =>
            ConfigAccessor.TryGetConfigValue(CoopOptions.FriendlyFireKey, out bool value) && value;

        /// <summary>
        /// How much health a downed player stands up with, as a percentage of their maximum, as set by
        /// the slider in the options window.
        ///
        /// **Only the host's copy of this decides**, same as friendly fire: a player who recovered to
        /// more health than their opponent would win fights they should have lost.
        ///
        /// Absent until the options window has been opened once, and absent reads as the protocol
        /// default rather than as zero - nobody means "stand up on nothing" by never opening a menu.
        /// </summary>
        public static byte RecoveryHealthPercent
        {
            get
            {
                if (!ConfigAccessor.TryGetConfigValue(CoopOptions.RecoveryHealthKey, out int value))
                {
                    return CoopProtocol.DefaultRecoveryHealthPercent;
                }

                return (byte)Math.Clamp(value, 1, 100);
            }
        }

        /// <summary>
        /// Creates the category and its entries. Called once at melon init.
        /// </summary>
        public static void Initialize()
        {
            MelonPreferences_Category category = MelonPreferences.CreateCategory("DrovaCoop");

            // A name rather than the relay's address, so the relay can be moved to another machine
            // without a mod release stranding everyone on an address nothing answers at.
            //
            // The name deliberately resolves to IPv4 only, even though the relay listens on both. The
            // transport resolves a name once and prefers IPv6 whenever the operating system reports
            // IPv6 support - which it does on any machine with the stack enabled, whether or not IPv6
            // actually routes anywhere. Publishing an AAAA would hand those players an address their
            // connection cannot reach, with no fallback and nothing on screen to say why: a UDP connect
            // into a void looks exactly like a relay that is down. An AAAA becomes safe once this side
            // resolves both families and falls back.
            _relayAddress = category.CreateEntry("RelayAddress", "relay.textos-dev.de", description: "Relay hostname or address");
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
