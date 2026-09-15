using MelonLoader;
using MelonLoader.Utils;

namespace DrovaCoopMod
{
    /// <summary>
    /// Turns individual patches off from outside the build, so a fault can be bisected against one
    /// savegame instead of one rebuild per guess.
    ///
    /// Set <c>HooksOff</c> in the DrovaCoop preferences, or the <c>DROVA_COOP_HOOKS_OFF</c> variable,
    /// to a comma-separated list of the names below, or to <c>all</c>. Anything not named stays on, so
    /// an empty setting is the shipping configuration.
    ///
    /// Read once at melon init, because a patch cannot be withdrawn after it is applied. Changing the
    /// variable means restarting the game.
    /// </summary>
    internal static class HookSwitches
    {
        /// <summary>The environment variable holding the names to disable.</summary>
        private const string Variable = "DROVA_COOP_HOOKS_OFF";

        /// <summary>The file under UserData holding the names to disable. Read before anything else.</summary>
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

        /// <summary>The saved-state hooks on scene objects. See <see cref="World.WorldObjects"/>.</summary>
        internal const string WorldObjects = "worldobjects";

        /// <summary>The crime-volume poll for teammates' bodies. See <see cref="World.GuardReach"/>.</summary>
        internal const string Guard = "guard";

        private static readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);

        private static bool _read;

        /// <summary>What was turned off, for <c>coop_status</c> and for reading a crash log.</summary>
        internal static string Disabled => _off.Count == 0 ? "nothing" : string.Join(", ", _off);

        /// <summary>
        /// Whether a patch should be applied. Call from the subsystem's own initialization, so the
        /// switch covers the patch rather than only the work it does.
        /// </summary>
        /// <param name="name">One of the name constants on this type.</param>
        internal static bool IsOn(string name)
        {
            Read();
            return !_off.Contains("all") && !_off.Contains(name);
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

            // The file first. MelonPreferences rewrites its own config on a clean exit from the values
            // it loaded at startup, so an edit made while the game is running is silently reverted -
            // and a bisect run against a setting that was reverted reads as a passing test. Nothing
            // owns this file but the person editing it.
            string? value = ReadFile();

            if (string.IsNullOrWhiteSpace(value))
            {
                value = CoopPreferences.HooksOff;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = Environment.GetEnvironmentVariable(Variable);
            }

            if (string.IsNullOrWhiteSpace(value)) return;

            foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                _off.Add(part.Trim());
            }

            if (_off.Count == 0) return;

            MelonLogger.Warning($"[Hooks] {Variable} is set, so these patches are NOT applied: " +
                $"{Disabled}. This is a diagnostic build configuration, not a session setting.");
        }
    }
}
