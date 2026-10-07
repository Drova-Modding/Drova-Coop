using MelonLoader;
using MelonLoader.Utils;

namespace DrovaCoopMod
{
    /// <summary>
    /// Turns patches off for bisecting: a comma-separated list of the names below, a system's type name (e.g. <c>Trade</c>), or <c>all</c>, in
    /// <c>UserData/DrovaCoopHooksOff.txt</c>. Read once at init.
    /// </summary>
    /// <remarks>A plain file, because MelonPreferences reverts edits made while the game runs.</remarks>
    internal static class HookSwitches
    {
        private const string FileName = "DrovaCoopHooksOff.txt";

        /// <summary>Shared rubble hauling. See <see cref="World.HaulShare"/>.</summary>
        internal const string Haul = "haul";

        /// <summary>The pile-naming postfix on the rubble spawner. Part of <see cref="World.HaulShare"/>.</summary>
        internal const string HaulSpawn = "haulspawn";

        /// <summary>The pickup postfix on looting. Part of <see cref="World.HaulShare"/>.</summary>
        internal const string HaulLoot = "haulloot";

        /// <summary>The relative-write prefix on every integer global. Part of <see cref="World.HaulShare"/>.</summary>
        internal const string HaulOperate = "haulop";

        /// <summary>The mine bed's day cursor. See <see cref="World.MineDayLoop"/>.</summary>
        internal const string MineDay = "mineday";

        /// <summary>The bound on a cutscene move. See <see cref="World.SceneMoveWatch"/>.</summary>
        internal const string SceneMove = "scenemove";

        /// <summary>The refusal of a client clock write. See <see cref="World.ClockGuard"/>.</summary>
        internal const string Clock = "clock";

        /// <summary>The routine gate's release on unload. See <see cref="World.NpcPlacement"/>.</summary>
        internal const string NpcUnload = "npcunload";

        /// <summary>The scaling of the party's blows on creatures. See <see cref="World.EnemyScaling"/>.</summary>
        internal const string Scaling = "scaling";

        /// <summary>The copy of a save slot taken before the game overwrites it. See <see cref="SaveBackups"/>.</summary>
        internal const string Backups = "backups";

        /// <summary>The shared-loot hooks on pickups and containers. See <see cref="World.LootShare"/>.</summary>
        internal const string Loot = "loot";

        /// <summary>Naming corpses and knocked-out bodies by creature. Part of <see cref="World.LootShare"/>.</summary>
        internal const string LootBodies = "lootbodies";

        /// <summary>The saved-state hooks on scene objects. See <see cref="World.WorldObjects"/>.</summary>
        internal const string WorldObjects = "worldobjects";

        /// <summary>The cache hooks. See <see cref="World.CacheShare"/>.</summary>
        internal const string Cache = "cache";

        /// <summary>The refusal of a puppet's own guard decision. See <see cref="World.BlockVerdicts"/>.</summary>
        internal const string Block = "block";

        /// <summary>The crime-volume poll for teammates' bodies. See <see cref="World.GuardReach"/>.</summary>
        internal const string Guard = "guard";

        /// <summary>The any-player answers to follow, wait and went-away checks. See <see cref="World.AnyPlayerReach"/>.</summary>
        internal const string Reach = "reach";

        /// <summary>Holding a world-started story dialogue for the player at the scene. See <see cref="World.StorySceneLease"/>.</summary>
        internal const string Lease = "lease";

        private static readonly HashSet<string> Off = new(StringComparer.OrdinalIgnoreCase);

        private static bool _read;

        /// <summary>What was turned off, for <c>coop_status</c> and for reading a crash log.</summary>
        internal static string Disabled => Off.Count == 0 ? "nothing" : string.Join(", ", Off);

        /// <summary>Whether a patch should be applied. Call before applying it, not inside it.</summary>
        /// <param name="name">One of the name constants on this type.</param>
        internal static bool IsOn(string name)
        {
            Read();
            return !Off.Contains("all") && !Off.Contains(name);
        }

        private static string? ReadFile()
        {
            try
            {
                string path = Path.Combine(MelonEnvironment.UserDataDirectory, FileName);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Hooks] {FileName} could not be read, so it was ignored: " + e.Message);
                return null;
            }
        }

        private static void Read()
        {
            if (_read) return;
            _read = true;

            string? value = ReadFile();

            if (string.IsNullOrWhiteSpace(value)) return;

            foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                Off.Add(part.Trim());
            }

            if (Off.Count == 0) return;

            MelonLogger.Warning($"[Hooks] {FileName} is set, so these patches are NOT applied: " +
                $"{Disabled}. This is a diagnostic build configuration, not a session setting.");
        }
    }
}
