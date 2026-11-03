using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Drova_Modding_API.Access;
using MelonLoader;

namespace DrovaCoopMod
{
    /// <summary>
    /// Everything the player can set, in one MelonPreferences category. Rule changes apply at once and
    /// reach the disk when the session window closes.
    /// </summary>
    public static class CoopPreferences
    {
        private static MelonPreferences_Entry<int>? _lanPort;
        private static MelonPreferences_Entry<bool> _friendlyFire = null!;
        private static MelonPreferences_Entry<bool> _standNearRevive = null!;
        private static MelonPreferences_Entry<bool> _hostLeadsStory = null!;
        private static MelonPreferences_Entry<int> _recoveryHealth = null!;
        private static MelonPreferences_Entry<int> _enemyHealth = null!;
        private static MelonPreferences_Entry<int> _enemyDamage = null!;
        private static MelonPreferences_Entry<bool> _sharedLoot = null!;
        private static MelonPreferences_Entry<bool> _arenaMode = null!;
        private static MelonPreferences_Entry<bool> _steamDirect = null!;
        private static MelonPreferences_Entry<bool> _portMapping = null!;
        private static MelonPreferences_Entry<bool> _publicGame = null!;
        private static MelonPreferences_Entry<int>? _saveBackups;
        private static MelonPreferences_Entry<string>? _versionCheckUrl;

        /// <summary>The UDP port a direct host listens on and LAN discovery looks at.</summary>
        public static int LanPort => Math.Clamp(_lanPort?.Value ?? LanGameInfo.DefaultPort, 1, 65535);

        /// <summary>Whether players can hurt each other. Only the host's value counts.</summary>
        public static bool FriendlyFire
        {
            get => _friendlyFire.Value;
            set => _friendlyFire.Value = value;
        }

        /// <summary>Whether standing over a downed teammate gets them up, besides the hold prompt. Only the host's value counts.</summary>
        public static bool StandNearRevive
        {
            get => _standNearRevive.Value;
            set => _standNearRevive.Value = value;
        }

        /// <summary>Whether only the host starts quest dialogues. Only the host's value counts.</summary>
        public static bool HostLeadsStory
        {
            get => _hostLeadsStory.Value;
            set => _hostLeadsStory.Value = value;
        }

        /// <summary>The health a downed player stands up with, in percent of maximum. Only the host's value counts.</summary>
        public static byte RecoveryHealthPercent
        {
            get => (byte)Math.Clamp(_recoveryHealth.Value, 1, 100);
            set => _recoveryHealth.Value = Math.Clamp((int)value, 1, 100);
        }

        /// <summary>Creature health in percent of authored. Only the host's value counts.</summary>
        public static ushort EnemyHealthPercent
        {
            get => (ushort)Math.Clamp(_enemyHealth.Value, CoopProtocol.MinEnemyHealthPercent, CoopProtocol.MaxEnemyHealthPercent);
            set => _enemyHealth.Value = Math.Clamp((int)value, CoopProtocol.MinEnemyHealthPercent, CoopProtocol.MaxEnemyHealthPercent);
        }

        /// <summary>How hard creatures hit players, in percent of authored. Only the host's value counts.</summary>
        public static ushort EnemyDamagePercent
        {
            get => (ushort)Math.Clamp(_enemyDamage.Value, CoopProtocol.MinEnemyDamagePercent, CoopProtocol.MaxEnemyDamagePercent);
            set => _enemyDamage.Value = Math.Clamp((int)value, CoopProtocol.MinEnemyDamagePercent, CoopProtocol.MaxEnemyDamagePercent);
        }

        /// <summary>Whether loot is one world's worth for the party. Only the host's value counts.</summary>
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

        /// <summary>Whether Steam sessions may connect directly. See <see cref="SteamAccess.AllowDirectConnections"/>.</summary>
        public static bool SteamDirect
        {
            get => _steamDirect.Value;
            set => _steamDirect.Value = value;
        }

        /// <summary>Whether a direct host opens its port on the router. See <see cref="NetworkAccess.UsePortMapping"/>.</summary>
        public static bool PortMapping
        {
            get => _portMapping.Value;
            set => _portMapping.Value = value;
        }

        /// <summary>Whether a Steam host is listed for players who are not friends. See <see cref="SteamAccess.OpenLobby"/>.</summary>
        public static bool PublicGame
        {
            get => _publicGame.Value;
            set => _publicGame.Value = value;
        }

        /// <summary><see cref="SharedLoot"/> as the byte the wire carries.</summary>
        public static byte LootMode => SharedLoot ? CoopProtocol.LootModeShared : CoopProtocol.LootModeInstanced;

        /// <summary>Where the VERSION policy is fetched from. Empty turns the check off.</summary>
        public static string VersionCheckUrl => _versionCheckUrl?.Value ?? VersionCheck.DefaultUrl;

        /// <summary>How many recent backups of each save slot to keep. Zero turns backups off.</summary>
        public static int SaveBackupsToKeep => Math.Max(0, _saveBackups?.Value ?? SaveBackups.DefaultKeep);

        /// <summary>Creates the category and its entries. Call once at melon init.</summary>
        public static void Initialize()
        {
            MelonPreferences_Category category = MelonPreferences.CreateCategory("DrovaCoop");

            _lanPort = category.CreateEntry("LanPort", LanGameInfo.DefaultPort, description: "UDP port for hosting on the local network or directly, and for finding such games");
            _friendlyFire = category.CreateEntry("FriendlyFire", false, description: "Players can hurt each other. Only the host's setting counts.");
            _standNearRevive = category.CreateEntry("StandNearRevive", false,
                description: "Standing over a downed teammate for a moment gets them up, besides holding the interact key on them. Only the host's setting counts.");
            _hostLeadsStory = category.CreateEntry("HostLeadsStory", false,
                description: "Only the host starts quest dialogues; the other players' dialogue options for quests are hidden. Only the host's setting counts.");
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

            _steamDirect = category.CreateEntry("SteamDirect", false,
                description: "Steam sessions try a direct connection before Valve's relay; both players need it on. Caution: the game freezes for about 2 seconds when a player joins, and players see each other's IP address.");

            _portMapping = category.CreateEntry("PortMapping", false,
                description: "Hosting directly asks the router to open the UDP port through UPnP, and closes it again when the session ends. The router must allow UPnP.");

            _publicGame = category.CreateEntry("PublicGame", false,
                description: "Hosting over Steam lists the game publicly: players who are not your friends find it and can join. Taken when hosting starts.");

            _saveBackups = category.CreateEntry("SaveBackups", SaveBackups.DefaultKeep,
                description: "How many recent copies of each save slot to keep under UserData/CoopBackups, taken before the game overwrites the slot. 0 turns backups off.");

            _versionCheckUrl = category.CreateEntry("VersionCheckUrl", VersionCheck.DefaultUrl,
                description: "Where the VERSION policy file is fetched from at start, which is how a build found to corrupt saves is withdrawn. Empty turns the check off.");
        }

        /// <summary>Writes the preferences file.</summary>
        public static void Save()
        {
            MelonPreferences.Save();
        }
    }
}
