using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.Session.Patches;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using Il2CppFileHandling.FileSystems;
using MelonLoader;
using MelonLoader.Utils;
using System.Reflection;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// Copies every save slot before the game overwrites it, since relayed writes have no inverse.
    /// Hooked on the private funnel <c>SavegameGameHandler.SaveCurrentAs</c>, which every save (autosave included) goes through.
    /// </summary>
    internal sealed class SaveBackups : ICoopSystem
    {
        internal const int DefaultKeep = 10;

        private const int SessionKeep = 3;

        // Under UserData, not beside the saves, so Steam Cloud does not upload the copies.
        private const string FolderName = "CoopBackups";

        private const string RecentPrefix = "recent_";

        private const string SessionPrefix = "session_";

        private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

        private static readonly string[] SlotFiles = ["data.sav", "meta.sav", "preview.png"];

        private static readonly Dictionary<string, int> AnchoredGeneration = new();

        private static int _sessionGeneration;

        string? ICoopSystem.HookSwitch => HookSwitches.Backups;

        internal static bool Hooked { get; private set; }

        /// <summary>Whether the patch has ever run. Distinguishes "never saved" from "inlined away".</summary>
        internal static bool HookFired { get; private set; }

        internal static int Made { get; private set; }

        internal static int Failed { get; private set; }

        internal static string Root => Path.Combine(MelonEnvironment.UserDataDirectory, Drova_Modding_API.Systems.Testing.LaunchProfile.UserDataFolder(FolderName));

        /// <summary>Applies the patch, resolved by signature because the overload taking the identifier is the funnel.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            MethodBase? target = HarmonyLib.AccessTools.Method(
                typeof(SavegameGameHandler),
                "SaveCurrentAs",
                [typeof(SavegameIdentifier), typeof(SaveOptions), typeof(bool)]);

            Hooked = Hooking.TryPrefix(harmony, target, typeof(SaveBackupsPatches), nameof(SaveBackupsPatches.SavePrefix),
                "SavegameGameHandler.SaveCurrentAs");
        }

        /// <summary>
        /// Marks a session start so the next save of each slot goes on the session shelf.
        /// Called on connect, not accept, because a save can land between the two.
        /// </summary>
        internal static void NoteSessionBegan()
        {
            _sessionGeneration++;

            CoopSaveMarks.Reset();
        }

        internal static void SavePrefix(SavegameGameHandler __instance, SavegameIdentifier id, SaveOptions saveOptions, bool ignoreRestriction)
        {
            try
            {
                HookFired = true;

                if (__instance != null && !__instance.DisableSaveSystem && (ignoreRestriction || __instance.IsSavingPossible(saveOptions)))
                {
                    Checkpoint.NoteSave(id);
                }

                if (CoopPreferences.SaveBackupsToKeep == 0) return;

                // The game's own refusals, so a declined save does not rotate a copy out.
                if (__instance == null || __instance.DisableSaveSystem) return;
                if (!ignoreRestriction && !__instance.IsSavingPossible(saveOptions)) return;

                if (id.IsDebug()) return;

                // Shares this prefix because a second, reentering patch on the save interface is fatal.
                CoopSaveMarks.Note(id.HeroIndex, id.SlotIndex);

                // Before the copy is taken, so the shelved slot is the one without this session in it.
                LedgerStore.Save();

                string shelf = ChooseShelf(id);
                TryBackup(id.HeroIndex, id.SlotIndex, shelf, out _);
            }
            catch (Exception e)
            {
                // Never let a backup stop a save.
                Failed++;
                MelonLogger.Error("[Backup] copying a save before it was overwritten failed: " + e);
            }
        }

        // The first save of a slot after a session began goes on the session shelf.
        private static string ChooseShelf(SavegameIdentifier id)
        {
            bool inSession = CoopSession.State != CoopSessionState.Idle && CoopSession.State != CoopSessionState.Failed;
            if (!inSession) return RecentPrefix;

            string key = SlotKey(id.HeroIndex, id.SlotIndex);

            if (AnchoredGeneration.TryGetValue(key, out int generation) && generation == _sessionGeneration)
            {
                return RecentPrefix;
            }

            AnchoredGeneration[key] = _sessionGeneration;
            return SessionPrefix;
        }

        private static bool TryBackup(int heroIndex, int slotIndex, string shelf, [NotNullWhen(true)] out string? folder)
        {
            folder = null;

            string slotPath = SlotPath(heroIndex, slotIndex);
            string data = Path.Combine(slotPath, SlotFiles[0]);

            if (!File.Exists(data)) return false;

            string slotRoot = BackupSlotRoot(heroIndex, slotIndex);
            string stamp = DateTime.Now.ToString(StampFormat);
            string destination = Path.Combine(slotRoot, shelf + stamp);

            // Two saves inside one second would share a name.
            int suffix = 1;
            while (Directory.Exists(destination))
            {
                destination = Path.Combine(slotRoot, shelf + stamp + "_" + suffix++);
            }

            Directory.CreateDirectory(destination);

            foreach (string name in SlotFiles)
            {
                string source = Path.Combine(slotPath, name);
                if (!File.Exists(source)) continue;

                File.Copy(source, Path.Combine(destination, name), overwrite: true);
            }

            Made++;
            folder = destination;

            int keep = shelf == SessionPrefix ? SessionKeep : CoopPreferences.SaveBackupsToKeep;
            Prune(slotRoot, shelf, keep);

            return true;
        }

        /// <summary>
        /// Puts a backup back into its slot, main menu only since a loaded world would autosave over it.
        /// The current slot is backed up first, so a restore is undoable.
        /// </summary>
        internal static bool TryRestore(int heroIndex, int slotIndex, string which, out string message)
        {
            if (Core.InGameplay)
            {
                message = "Restore from the main menu only. With a world loaded, the game would write the slot back over the restored files.";
                return false;
            }

            // Immersive and Hardcore keep copies for a crash, not for a rewind.
            byte? mode = SavegameLoader.Get()?.GetLatestHeroMetaInfoSync(heroIndex)?.GameMode;
            if (mode == (byte)GameMode.Immersive || mode == (byte)GameMode.Hardcore)
            {
                message = $"Hero {heroIndex} plays in {(GameMode)mode.Value} mode, which rules out going back to an older save. Not restored.";
                return false;
            }

            string slotRoot = BackupSlotRoot(heroIndex, slotIndex);
            string? source = FindBackup(slotRoot, which);

            if (source == null)
            {
                message = $"No backup '{which}' for Hero_{heroIndex:00}/Savegame_{slotIndex:00} under {slotRoot}.";
                return false;
            }

            string slotPath = SlotPath(heroIndex, slotIndex);
            Directory.CreateDirectory(slotPath);

            string undo = TryBackup(heroIndex, slotIndex, RecentPrefix, out string? undoFolder)
                ? $" The slot as it was is on the recent shelf as {Path.GetFileName(undoFolder)}."
                : string.Empty;

            foreach (string name in SlotFiles)
            {
                string file = Path.Combine(source, name);
                if (!File.Exists(file)) continue;

                File.Copy(file, Path.Combine(slotPath, name), overwrite: true);
            }

            message = $"Restored {Path.GetFileName(source)} into Hero_{heroIndex:00}/Savegame_{slotIndex:00}.{undo}";
            return true;
        }

        /// <summary>One line per slot that has backups, for <c>coop_backups</c>.</summary>
        internal static List<string> Describe()
        {
            List<string> lines = new();

            if (!Directory.Exists(Root)) return lines;

            foreach (string heroDir in Directory.GetDirectories(Root).OrderBy(path => path, StringComparer.Ordinal))
            {
                foreach (string slotDir in Directory.GetDirectories(heroDir).OrderBy(path => path, StringComparer.Ordinal))
                {
                    string[] recent = Shelf(slotDir, RecentPrefix);
                    string[] session = Shelf(slotDir, SessionPrefix);
                    if (recent.Length == 0 && session.Length == 0) continue;

                    string newestRecent = recent.Length > 0 ? Path.GetFileName(recent[0]) : "none";
                    string newestSession = session.Length > 0 ? Path.GetFileName(session[0]) : "none";

                    lines.Add($"{Path.GetFileName(heroDir)}/{Path.GetFileName(slotDir)}: {recent.Length} recent (newest {newestRecent}), " +
                        $"{session.Length} pre-session (newest {newestSession})");
                }
            }

            return lines;
        }

        private static string SlotPath(int heroIndex, int slotIndex)
        {
            return Path.GetFullPath(DefaultFileSaveSystemHelper.GetSavegamePath(heroIndex, slotIndex));
        }

        private static string BackupSlotRoot(int heroIndex, int slotIndex)
        {
            return Path.Combine(Root, $"Hero_{heroIndex:00}", $"Savegame_{slotIndex:00}");
        }

        private static string SlotKey(int heroIndex, int slotIndex)
        {
            return heroIndex + "/" + slotIndex;
        }

        // Newest first: names carry the timestamp, so descending ordinal order is age order.
        private static string[] Shelf(string slotRoot, string shelf)
        {
            if (!Directory.Exists(slotRoot)) return Array.Empty<string>();

            return Directory.GetDirectories(slotRoot, shelf + "*")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
        }

        private static string? FindBackup(string slotRoot, string which)
        {
            if (string.Equals(which, "latest", StringComparison.OrdinalIgnoreCase))
            {
                string[] recent = Shelf(slotRoot, RecentPrefix);
                return recent.Length > 0 ? recent[0] : null;
            }

            if (string.Equals(which, "session", StringComparison.OrdinalIgnoreCase))
            {
                string[] session = Shelf(slotRoot, SessionPrefix);
                return session.Length > 0 ? session[0] : null;
            }

            // A name, never a path, so the command cannot reach outside the backup root.
            if (which.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || which.Contains("..")) return null;

            string exact = Path.Combine(slotRoot, which);
            return Directory.Exists(exact) ? exact : null;
        }

        private static void Prune(string slotRoot, string shelf, int keep)
        {
            string[] folders = Shelf(slotRoot, shelf);

            for (int index = keep; index < folders.Length; index++)
            {
                try
                {
                    Directory.Delete(folders[index], recursive: true);
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[Backup] an old backup could not be removed: " + e.Message);
                }
            }
        }
    }
}
