using DrovaCoopMod.Session;
using MelonLoader;
using MelonLoader.Utils;

namespace DrovaCoopMod
{
    /// <summary>
    /// Remembers which save slots a co-op session has written into, and says so when one of them is
    /// played alone.
    ///
    /// A session's writes land in an ordinary savegame and are indistinguishable from the player's own
    /// once they are there: a quest flag pushed from another machine, a creature somebody else killed, a
    /// day another player slept through. None of it has an inverse. The slot is still a perfectly good
    /// savegame; what it is not is a record of what this player did, and a player who takes it into a
    /// solo run has no way to tell.
    ///
    /// So the slot is marked rather than renamed. Renaming - the distinct extension this was first
    /// planned as - would take the slot out of the game's own menu, because the game enumerates what it
    /// finds on disk, and a save that vanishes is a worse outcome than one that is quietly shared. The
    /// mark is a small file of this mod's own, kept under UserData rather than beside the save for the
    /// same reason the backups are: the save folder is what Steam Cloud carries.
    ///
    /// Nothing here blocks anything. It writes a mark, and it says once per slot per run that the slot
    /// has one when the player saves over it outside a session, which is the moment it matters.
    /// </summary>
    internal static class CoopSaveMarks
    {
        /// <summary>The folder under UserData that holds the marks.</summary>
        internal const string FolderName = "CoopSaves";

        private static readonly HashSet<string> _warned = new(StringComparer.Ordinal);

        /// <summary>Where the marks live.</summary>
        internal static string Root => Path.Combine(MelonEnvironment.UserDataDirectory, FolderName);

        /// <summary>How many slots this run has marked as touched by a session.</summary>
        internal static int Marked { get; private set; }

        /// <summary>How many times a marked slot was saved over outside a session.</summary>
        internal static int Warnings { get; private set; }

        /// <summary>
        /// Called for every save the game is actually going to write. Marks the slot when a session is
        /// running, and says something when one that was marked earlier is being written alone.
        /// </summary>
        /// <param name="heroIndex">The hero the slot belongs to.</param>
        /// <param name="slotIndex">The slot.</param>
        internal static void Note(int heroIndex, int slotIndex)
        {
            try
            {
                // A session actually carrying state, not one that merely asked for one. Connecting covers a
                // join the relay has not answered yet, and a save landing in that window would mark the
                // slot for good over a session that never happened.
                bool inSession = CoopSession.IsLive;

                if (inSession)
                {
                    Mark(heroIndex, slotIndex);
                    return;
                }

                WarnIfMarked(heroIndex, slotIndex);
            }
            catch (Exception e)
            {
                // Never let this stop a save, for the same reason the backups do not.
                MelonLogger.Error("[CoopSave] marking a slot failed: " + e);
            }
        }

        /// <summary>Whether a slot has ever been written to by a session this mod saw.</summary>
        internal static bool IsMarked(int heroIndex, int slotIndex)
        {
            return File.Exists(MarkPath(heroIndex, slotIndex));
        }

        /// <summary>
        /// One line per marked slot, for <c>coop_status</c> and for the console command. Empty when
        /// nothing has been marked.
        /// </summary>
        internal static List<string> Describe()
        {
            List<string> lines = new();

            if (!Directory.Exists(Root)) return lines;

            foreach (string file in Directory.EnumerateFiles(Root, "*.txt", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
                string first = string.Empty;

                try
                {
                    string[] read = File.ReadAllLines(file);
                    if (read.Length > 0) first = read[0];
                }
                catch (Exception)
                {
                    // A mark that cannot be read still says the slot was in a session, which is the part
                    // that matters; the timestamp is a convenience.
                }

                lines.Add(first.Length > 0 ? $"{relative}: {first}" : relative);
            }

            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        /// <summary>Forgets what has been said this run, so a new session warns again.</summary>
        internal static void Reset()
        {
            _warned.Clear();
        }

        private static void Mark(int heroIndex, int slotIndex)
        {
            string path = MarkPath(heroIndex, slotIndex);

            // Written once and then left alone. The first time a session wrote this slot is the useful
            // fact; rewriting it on every autosave would only move the timestamp forward and turn a
            // marker into a write on every save.
            if (File.Exists(path)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                $"first written by a coop session {DateTime.Now:yyyy-MM-dd HH:mm:ss}, mod {BuildInfo.Version}"
                + Environment.NewLine
                + "This slot holds world state from a co-op session: quest flags, kills and time that"
                + Environment.NewLine
                + "another player's world produced. Deleting this file only removes the note."
                + Environment.NewLine);

            Marked++;
            MelonLogger.Msg($"[CoopSave] hero {heroIndex} slot {slotIndex} now holds co-op history.");
        }

        private static void WarnIfMarked(int heroIndex, int slotIndex)
        {
            if (!IsMarked(heroIndex, slotIndex)) return;

            // Once per slot per run. This fires on every autosave otherwise, and a warning that repeats
            // is one nobody reads.
            string key = heroIndex + "/" + slotIndex;
            if (!_warned.Add(key)) return;

            Warnings++;
            MelonLogger.Warning($"[CoopSave] hero {heroIndex} slot {slotIndex} carries co-op history and is " +
                "being played alone; what a session wrote is now part of this save. " +
                "coop_backups lists the copies from before the session.");
        }

        private static string MarkPath(int heroIndex, int slotIndex)
        {
            return Path.Combine(Root, $"Hero_{heroIndex:00}", $"Savegame_{slotIndex:00}.txt");
        }
    }
}
