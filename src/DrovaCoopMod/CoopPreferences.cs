using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using MelonLoader;

namespace DrovaCoopMod
{
    /// <summary>
    /// Everything the player can set, in one MelonPreferences category. Rule changes apply at once and
    /// reach the disk when the session window closes.
    /// </summary>
    public static class CoopPreferences
    {
        private static MelonPreferences_Entry<string> _relayAddress = null!;
        private static MelonPreferences_Entry<int> _relayPort = null!;
        private static MelonPreferences_Entry<string> _sessionCode = null!;
        private static MelonPreferences_Entry<int>? _lanPort;
        private static MelonPreferences_Entry<string> _sessionPassword = null!;
        private static MelonPreferences_Entry<bool> _friendlyFire = null!;
        private static MelonPreferences_Entry<int> _recoveryHealth = null!;
        private static MelonPreferences_Entry<int> _enemyHealth = null!;
        private static MelonPreferences_Entry<int> _enemyDamage = null!;
        private static MelonPreferences_Entry<bool> _sharedLoot = null!;
        private static MelonPreferences_Entry<bool> _arenaMode = null!;
        private static MelonPreferences_Entry<int>? _saveBackups;
        private static MelonPreferences_Entry<string>? _versionCheckUrl;

        /// <summary>Relay hostname or address.</summary>
        public static string RelayAddress => _relayAddress.Value;

        /// <summary>Relay UDP port.</summary>
        public static int RelayPort => _relayPort.Value;

        /// <summary>The UDP port a direct host listens on and LAN discovery looks at.</summary>
        public static int LanPort => Math.Clamp(_lanPort?.Value ?? LanGameInfo.DefaultPort, 1, 65535);

        /// <summary>The configured session code, possibly empty.</summary>
        public static string SessionCode => _sessionCode.Value;

        /// <summary>The configured session password, possibly empty.</summary>
        public static string SessionPassword => _sessionPassword.Value;

        /// <summary>
        /// Whether players can hurt each other. Only the host's value reaches the session.
        /// </summary>
        public static bool FriendlyFire
        {
            get => _friendlyFire.Value;
            set => _friendlyFire.Value = value;
        }

        /// <summary>
        /// The health a downed player stands up with, in percent of maximum. Only the host's value counts.
        /// </summary>
        public static byte RecoveryHealthPercent
        {
            get => (byte)Math.Clamp(_recoveryHealth.Value, 1, 100);
            set => _recoveryHealth.Value = Math.Clamp((int)value, 1, 100);
        }

        /// <summary>
        /// Creature health in percent of authored. Only the host's value counts.
        /// </summary>
        public static ushort EnemyHealthPercent
        {
            get => (ushort)Math.Clamp(_enemyHealth.Value, CoopProtocol.MinEnemyHealthPercent, CoopProtocol.MaxEnemyHealthPercent);
            set => _enemyHealth.Value = Math.Clamp((int)value, CoopProtocol.MinEnemyHealthPercent, CoopProtocol.MaxEnemyHealthPercent);
        }

        /// <summary>
        /// How hard creatures hit players, in percent of authored. Only the host's value counts.
        /// </summary>
        public static ushort EnemyDamagePercent
        {
            get => (ushort)Math.Clamp(_enemyDamage.Value, CoopProtocol.MinEnemyDamagePercent, CoopProtocol.MaxEnemyDamagePercent);
            set => _enemyDamage.Value = Math.Clamp((int)value, CoopProtocol.MinEnemyDamagePercent, CoopProtocol.MaxEnemyDamagePercent);
        }

        /// <summary>
        /// Whether loot is one world's worth for the party. Only the host's value reaches the session.
        /// </summary>
        public static bool SharedLoot
        {
            get => _sharedLoot.Value;
            set => _sharedLoot.Value = value;
        }

        /// <summary>Whether a session hosted from here is in arena mode. See <see cref="SessionRulesMessage.ArenaMode"/>.</summary>
        public static bool ArenaMode
        {
            get => _arenaMode.Value;
            set => _arenaMode.Value = value;
        }

        /// <summary>
        /// <see cref="SharedLoot"/> as the byte the wire carries.
        /// </summary>
        public static byte LootMode => SharedLoot ? CoopProtocol.LootModeShared : CoopProtocol.LootModeInstanced;

        /// <summary>Where the VERSION policy is fetched from. Empty turns the check off.</summary>
        public static string VersionCheckUrl => _versionCheckUrl?.Value ?? VersionCheck.DefaultUrl;

        /// <summary>How many recent backups of each save slot to keep. Zero turns backups off.</summary>
        public static int SaveBackupsToKeep => Math.Max(0, _saveBackups?.Value ?? SaveBackups.DefaultKeep);

        /// <summary>
        /// Creates the category and its entries. Call once at melon init.
        /// </summary>
        public static void Initialize()
        {
            MelonPreferences_Category category = MelonPreferences.CreateCategory("DrovaCoop");

            // The name resolves to IPv4 only. The transport prefers IPv6 whenever the OS reports it,
            // with no fallback, so an AAAA record would strand players whose IPv6 does not route.
            _relayAddress = category.CreateEntry("RelayAddress", "relay.textos-dev.de", description: "Relay hostname or address");
            _relayPort = category.CreateEntry("RelayPort", 9060, description: "Relay UDP port");
            _sessionCode = category.CreateEntry("SessionCode", string.Empty, description: "The last code joined, used by coop_join without a code. Hosting never uses it");
            _lanPort = category.CreateEntry("LanPort", LanGameInfo.DefaultPort, description: "UDP port for hosting on the local network or directly, and for finding such games");
            _sessionPassword = category.CreateEntry("SessionPassword", string.Empty, description: "Session password. Through the relay it also encrypts end to end; a direct session is never encrypted");
            _friendlyFire = category.CreateEntry("FriendlyFire", false, description: "Players can hurt each other. Only the host's setting counts.");
            _recoveryHealth = category.CreateEntry("RecoveryHealth", (int)CoopProtocol.DefaultRecoveryHealthPercent,
                description: "Percent of maximum health a downed player stands up with, 1-100. Only the host's setting counts.");
            _enemyHealth = category.CreateEntry("EnemyHealth", (int)CoopProtocol.DefaultEnemyHealthPercent,
                description: $"Creature health in percent of normal, {CoopProtocol.MinEnemyHealthPercent}-{CoopProtocol.MaxEnemyHealthPercent}. The party's blows are scaled, the save is untouched. Only the host's setting counts.");
            _enemyDamage = category.CreateEntry("EnemyDamage", (int)CoopProtocol.DefaultEnemyDamagePercent,
                description: $"How hard creatures hit players, in percent of normal, {CoopProtocol.MinEnemyDamagePercent}-{CoopProtocol.MaxEnemyDamagePercent}. Only the host's setting counts.");
            _sharedLoot = category.CreateEntry("SharedLoot", false,
                description: "One world's loot for the party: a pickup taken is gone for everyone, a chest holds what the last player left. Only the host's setting counts.");

            _arenaMode = category.CreateEntry("ArenaMode", false,
                description: "Host in arena mode: everyone keeps their own world, quests and faction, and only fighting each other is shared (bodies, blows, summons, the arena). Taken when hosting starts.");

            _saveBackups = category.CreateEntry("SaveBackups", SaveBackups.DefaultKeep,
                description: "How many recent copies of each save slot to keep under UserData/CoopBackups, taken before the game overwrites the slot. 0 turns backups off.");

            _versionCheckUrl = category.CreateEntry("VersionCheckUrl", VersionCheck.DefaultUrl,
                description: "Where the VERSION policy file is fetched from at start, which is how a build found to corrupt saves is withdrawn. Empty turns the check off.");
        }

        /// <summary>The configured session code, or null when there is none.</summary>
        public static string? SessionCodeOrNull()
        {
            return string.IsNullOrWhiteSpace(_sessionCode.Value) ? null : _sessionCode.Value.Trim();
        }

        /// <summary>The configured password, or null when there is none.</summary>
        public static string? PasswordOrNull()
        {
            return string.IsNullOrEmpty(_sessionPassword.Value) ? null : _sessionPassword.Value;
        }

        /// <summary>
        /// Stores and saves the code and password typed into the session window.
        /// </summary>
        public static void RememberSession(string? code, string? password)
        {
            _sessionCode.Value = code ?? string.Empty;
            _sessionPassword.Value = password ?? string.Empty;
            Save();
        }

        /// <summary>Writes the preferences file.</summary>
        public static void Save()
        {
            MelonPreferences.Save();
        }
    }
}
