using MelonLoader;
using MelonLoader.Utils;

namespace DrovaCoopMod
{
    /// <summary>
    /// Turns individual patches off from outside the build, so a fault can be bisected against one
    /// savegame instead of one rebuild per guess.
    ///
    /// Write a comma-separated list of the names below, or <c>all</c>, into
    /// <c>UserData/DrovaCoopHooksOff.txt</c>. Anything not named stays on, so no file is the shipping
    /// configuration.
    ///
    /// A plain file rather than a MelonPreferences entry, because MelonPreferences rewrites its config
    /// on a clean exit from the values it loaded at startup. An edit made while the game runs is silently
    /// reverted, and a bisect run against a reverted setting reads as a passing test.
    ///
    /// Read once at melon init, because a patch cannot be withdrawn after it is applied. Changing the
    /// file means restarting the game.
    /// </summary>
    internal static class HookSwitches
    {
        /// <summary>The file under UserData holding the names to disable.</summary>
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

        /// <summary>
        /// Naming corpses and knocked-out bodies by the creature they were. Part of
        /// <see cref="World.LootShare"/>, and separable from it because it is the half that depends on
        /// the creature registry rather than on save roots.
        /// </summary>
        internal const string LootBodies = "lootbodies";

        /// <summary>The saved-state hooks on scene objects. See <see cref="World.WorldObjects"/>.</summary>
        internal const string WorldObjects = "worldobjects";

        /// <summary>
        /// The cache hooks: sharing what one spilled, and keeping its pickups out of the loot ledger.
        /// See <see cref="World.CacheShare"/>. Turning this off restores the ledger noise it repairs.
        /// </summary>
        internal const string Cache = "cache";

        /// <summary>The refusal of a puppet's own guard decision. See <see cref="World.BlockVerdicts"/>.</summary>
        internal const string Block = "block";

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

            string? value = ReadFile();

            if (string.IsNullOrWhiteSpace(value)) return;

            foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                _off.Add(part.Trim());
            }

            if (_off.Count == 0) return;

            MelonLogger.Warning($"[Hooks] {FileName} is set, so these patches are NOT applied: " +
                $"{Disabled}. This is a diagnostic build configuration, not a session setting.");
        }
    }
}
