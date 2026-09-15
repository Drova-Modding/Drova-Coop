using System.Reflection;
using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using Il2CppFileHandling.FileSystems;
using MelonLoader;
using MelonLoader.Utils;

namespace DrovaCoopMod
{
    /// <summary>
    /// Keeps a copy of every save slot from before the game overwrites it.
    ///
    /// The host's save is the world, and everything this mod relays ends up in it: a creature killed on
    /// a teammate's screen, a quest flag pushed from another machine, a day advanced by somebody else's
    /// sleep. None of those writes has an inverse, so the only way back from a save the session got
    /// wrong is a copy from before it. The game keeps none.
    ///
    /// The copy is taken in a prefix on <c>SavegameGameHandler.SaveCurrentAs</c>, the private funnel
    /// every save goes through - the menu, the quick save, and the autosave that never raises the
    /// modding API's own event - and it is taken before <c>SaveTextures</c> replaces the preview, so a
    /// backup is the slot exactly as it was. The copy is synchronous: the game serializes on a thread for
    /// an autosave, and a copy still running when the write starts would be half of two saves.
    ///
    /// Two shelves per slot, both pruned by age. The recent shelf is the last few saves whatever was
    /// happening; the session shelf is the first save after a session began, which is the last save
    /// from before the session touched the slot and the one worth keeping when the recent shelf has
    /// rotated past it. Backups live under UserData rather than beside the saves, because the save
    /// folder is what Steam Cloud carries and a ring of copies would be uploaded with it.
    /// </summary>
    internal static class SaveBackups
    {
        /// <summary>How many recent copies each slot keeps unless the preference says otherwise.</summary>
        internal const int DefaultKeep = 10;

        /// <summary>How many pre-session copies each slot keeps. Fixed: these are rare and small.</summary>
        internal const int SessionKeep = 3;

        /// <summary>The folder under UserData that holds every backup.</summary>
        internal const string FolderName = "CoopBackups";

        private const string RecentPrefix = "recent_";

        private const string SessionPrefix = "session_";

        private const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

        private static readonly string[] SlotFiles = { "data.sav", "meta.sav", "preview.png" };

        private static bool _hooked;

        private static bool _hookFired;

        private static int _made;

        private static int _failed;

        private static int _sessionGeneration;

        private static readonly Dictionary<string, int> _anchoredGeneration = new();

        /// <summary>Whether the patch applied. False means no save is being copied.</summary>
        internal static bool Hooked => _hooked;

        /// <summary>Whether the patch has ever run. Distinguishes "never saved" from "inlined away".</summary>
        internal static bool HookFired => _hookFired;

        /// <summary>How many backups this run has written. For <c>coop_status</c>.</summary>
        internal static int Made => _made;

        /// <summary>How many backups this run failed to write. Anything but zero is worth reading the log for.</summary>
        internal static int Failed => _failed;

        /// <summary>Where the backups are, so a player can be told rather than made to look.</summary>
        internal static string Root => Path.Combine(MelonEnvironment.UserDataDirectory, FolderName);

        /// <summary>
        /// Applies the patch. Called once at melon init.
        ///
        /// Resolved by signature, because the name has more than one overload on the handler and the one
        /// with the identifier is the funnel the others flow into.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            MethodBase? target = HarmonyLib.AccessTools.Method(
                typeof(SavegameGameHandler),
                "SaveCurrentAs",
                new[] { typeof(SavegameIdentifier), typeof(SaveOptions), typeof(bool) });

            _hooked = Hooking.TryPrefix(harmony, target, typeof(SaveBackups), nameof(SavePrefix),
                "SavegameGameHandler.SaveCurrentAs");
        }

        /// <summary>
        /// Marks the start of a session, so the next save of every slot is kept on the session shelf.
        /// Called when the session begins connecting rather than when it is accepted, because the
        /// first save is what matters and a save can land between the two.
        /// </summary>
        internal static void NoteSessionBegan()
        {
            _sessionGeneration++;

            // A slot warned about before this session is worth warning about again after it: the session
            // about to start is new history, and the warning is per run only so it does not repeat on
            // every autosave.
            CoopSaveMarks.Reset();
        }

        /// <summary>
        /// Copies the slot the game is about to write, if the game is actually going to write it.
        /// </summary>
        private static void SavePrefix(SavegameGameHandler __instance, SavegameIdentifier id, SaveOptions saveOptions, bool ignoreRestriction)
        {
            try
            {
                _hookFired = true;

                if (CoopPreferences.SaveBackupsToKeep == 0) return;

                // The same refusals the game makes before it writes, so a save it turns down does not
                // rotate a copy out of the ring for nothing.
                if (__instance == null || __instance.DisableSaveSystem) return;
                if (!ignoreRestriction && !__instance.IsSavingPossible(saveOptions)) return;

                // Debug and report slots are negative indices the game uses for its own tooling.
                if (id.IsDebug()) return;

                // Hung off this prefix rather than given a patch of its own. Both want the same moment -
                // a save the game has decided to make - and the save interface is the one place where a
                // second patch that reenters the first has been fatal before.
                CoopSaveMarks.Note(id.HeroIndex, id.SlotIndex);

                string shelf = ChooseShelf(id);
                TryBackup(id.HeroIndex, id.SlotIndex, shelf, out _);
            }
            catch (Exception e)
            {
                // Never let a backup stop a save. A save that did not happen is worse than one with no
                // copy behind it.
                _failed++;
                MelonLogger.Error("[Backup] copying a save before it was overwritten failed: " + e);
            }
        }

        /// <summary>
        /// Which shelf a save of this slot belongs on: the session shelf for the first save of a slot
        /// after a session began, the recent shelf for every other.
        /// </summary>
        private static string ChooseShelf(SavegameIdentifier id)
        {
            bool inSession = CoopSession.State != CoopSessionState.Idle && CoopSession.State != CoopSessionState.Failed;
            if (!inSession) return RecentPrefix;

            string key = SlotKey(id.HeroIndex, id.SlotIndex);

            if (_anchoredGeneration.TryGetValue(key, out int generation) && generation == _sessionGeneration)
            {
                return RecentPrefix;
            }

            _anchoredGeneration[key] = _sessionGeneration;
            return SessionPrefix;
        }

        /// <summary>
        /// Copies one slot to a new backup folder and prunes that slot's shelf.
        /// </summary>
        /// <param name="heroIndex">The hero the slot belongs to.</param>
        /// <param name="slotIndex">The slot.</param>
        /// <param name="shelf">The shelf prefix, recent or session.</param>
        /// <param name="folder">Where the copy landed, or null when there was nothing to copy.</param>
        /// <returns>Whether a copy was written.</returns>
        internal static bool TryBackup(int heroIndex, int slotIndex, string shelf, out string? folder)
        {
            folder = null;

            string slotPath = SlotPath(heroIndex, slotIndex);
            string data = Path.Combine(slotPath, SlotFiles[0]);

            // A slot being written for the first time has nothing worth keeping yet.
            if (!File.Exists(data)) return false;

            string slotRoot = BackupSlotRoot(heroIndex, slotIndex);
            string stamp = DateTime.Now.ToString(StampFormat);
            string destination = Path.Combine(slotRoot, shelf + stamp);

            // Two saves inside one second - a quick save on top of an autosave - would share a name.
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

            _made++;
            folder = destination;

            int keep = shelf == SessionPrefix ? SessionKeep : CoopPreferences.SaveBackupsToKeep;
            Prune(slotRoot, shelf, keep);

            return true;
        }

        /// <summary>
        /// Puts a backup back into its slot. Only from the main menu: with a world up the game holds the
        /// slot in memory and would write it straight back over the restored files at the next autosave.
        /// The slot as it is now goes on the recent shelf first, so a restore is itself undoable.
        /// </summary>
        /// <param name="heroIndex">The hero the slot belongs to.</param>
        /// <param name="slotIndex">The slot.</param>
        /// <param name="which">
        /// A backup folder name, or <c>latest</c> for the newest recent copy, or <c>session</c> for the
        /// newest pre-session copy.
        /// </param>
        /// <param name="message">What happened, in words for the player.</param>
        internal static bool TryRestore(int heroIndex, int slotIndex, string which, out string message)
        {
            if (Core.InGameplay)
            {
                message = "Restore from the main menu only. With a world loaded, the game would write the slot back over the restored files.";
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

            string undo = TryBackup(heroIndex, slotIndex, RecentPrefix, out string? undoFolder) && undoFolder != null
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

        /// <summary>
        /// One line per slot that has backups, for <c>coop_backups</c>. Empty when there are none.
        /// </summary>
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

        /// <summary>
        /// The folder the game keeps this slot in, asked of the game's own path helper so the answer
        /// follows whatever build folder and separator the game uses.
        /// </summary>
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

        /// <summary>
        /// The backup folders on one shelf, newest first. Names carry the timestamp, so ordinal order
        /// descending is age order.
        /// </summary>
        private static string[] Shelf(string slotRoot, string shelf)
        {
            if (!Directory.Exists(slotRoot)) return Array.Empty<string>();

            return Directory.GetDirectories(slotRoot, shelf + "*")
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
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

            // A folder name as printed by coop_backups. Kept to a name, never a path, so the command
            // cannot be pointed at anything outside the backup root.
            if (which.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || which.Contains("..")) return null;

            string exact = Path.Combine(slotRoot, which);
            return Directory.Exists(exact) ? exact : null;
        }

        /// <summary>
        /// Deletes the oldest folders on a shelf beyond what it keeps. Only this mod's own folders, by
        /// prefix, under this mod's own root.
        /// </summary>
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
