using MelonLoader;
using MelonLoader.Utils;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// Marks save slots a co-op session wrote into and warns when one is saved alone. Marks live under
    /// UserData, not beside the save, because Steam Cloud carries the save folder.
    /// </summary>
    internal static class CoopSaveMarks
    {
        /// <summary>The folder under UserData that holds the marks.</summary>
        internal const string FolderName = "CoopSaves";

        private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);

        internal static string Root => Path.Combine(MelonEnvironment.UserDataDirectory, Drova_Modding_API.Systems.Testing.LaunchProfile.UserDataFolder(FolderName));

        /// <summary>How many slots this run has marked as touched by a session.</summary>
        internal static int Marked { get; private set; }

        /// <summary>How many times a marked slot was saved over outside a session.</summary>
        internal static int Warnings { get; private set; }

        /// <summary>
        /// Called for every save the game writes. Marks the slot in a session, warns about a marked one outside.
        /// </summary>
        internal static void Note(int heroIndex, int slotIndex)
        {
            try
            {
                // Live, not merely connecting, so a join that never completes marks nothing.
                bool inSession = CoopSession.SharesWorld;

                if (inSession)
                {
                    Mark(heroIndex, slotIndex);
                    return;
                }

                WarnIfMarked(heroIndex, slotIndex);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[CoopSave] marking a slot failed: " + e);
            }
        }

        internal static bool IsMarked(int heroIndex, int slotIndex)
        {
            return File.Exists(MarkPath(heroIndex, slotIndex));
        }

        /// <summary>One line per marked slot, for <c>coop_status</c> and the console command.</summary>
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
                    // An unreadable mark still marks the slot. The timestamp is a convenience.
                }

                lines.Add(first.Length > 0 ? $"{relative}: {first}" : relative);
            }

            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        /// <summary>Forgets what has been said this run, so a new session warns again.</summary>
        internal static void Reset()
        {
            Warned.Clear();
        }

        private static void Mark(int heroIndex, int slotIndex)
        {
            string path = MarkPath(heroIndex, slotIndex);

            // Written once, so it keeps the first session's timestamp.
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

            // Once per slot per run, or it fires on every autosave.
            string key = heroIndex + "/" + slotIndex;
            if (!Warned.Add(key)) return;

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
