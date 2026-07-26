using MelonLoader;

namespace DrovaCoopMod.Session
{
    /// <summary>
    /// Identifies the set of mods this game is running, so two players can notice they are not running
    /// the same ones.
    ///
    /// Worth checking because a mod that changes items, stats, or the world changes them on one side
    /// only. The result is not a crash but a slow disagreement about what is in the world, which surfaces
    /// as the co-op mod appearing to lose things.
    /// </summary>
    internal static class ModSet
    {
        private static uint _hash;
        private static bool _computed;

        /// <summary>
        /// A hash of the loaded mods and their versions, stable across runs and across machines.
        /// </summary>
        internal static uint Hash()
        {
            if (_computed) return _hash;

            _computed = true;

            try
            {
                // Sorted before hashing, because load order is not the same on two machines and is not
                // what is being compared. Unsorted, two players with identical mods in a different order
                // would be told they disagree.
                List<string> entries = Describe();
                entries.Sort(StringComparer.Ordinal);

                // FNV-1a over the joined text. Not a cryptographic hash and not asked to be one: this
                // detects an accident, and anyone willing to forge it can simply lie about the whole
                // handshake.
                unchecked
                {
                    uint hash = 2166136261u;
                    foreach (char character in string.Join("\n", entries))
                    {
                        hash = (hash ^ character) * 16777619u;
                    }

                    _hash = hash;
                }
            }
            catch (Exception e)
            {
                // Zero is the agreed "did not compute one", and the far side treats it as no opinion
                // rather than as a mismatch.
                _hash = 0u;
                MelonLogger.Warning("[Coop] Could not read the mod list; the mod set will not be compared: " + e.Message);
            }

            return _hash;
        }

        /// <summary>
        /// The loaded mods, one line each, for printing when two sides disagree.
        ///
        /// Each side prints its own rather than sending it: the list would otherwise be attacker-chosen
        /// text that has to be bounded and sanitized before it reaches a log, to say something the two
        /// players can work out by comparing their own.
        /// </summary>
        internal static List<string> Describe()
        {
            List<string> entries = new();

            foreach (MelonMod mod in MelonMod.RegisteredMelons)
            {
                entries.Add($"{mod.Info.Name} {mod.Info.Version}");
            }

            return entries;
        }

        /// <summary>
        /// Writes the local mod list to the log, for when a mismatch has been reported.
        /// </summary>
        internal static void Report()
        {
            List<string> entries = Describe();
            entries.Sort(StringComparer.Ordinal);

            MelonLogger.Msg($"[Coop] This game runs {entries.Count} mods (hash {Hash():X8}):");
            foreach (string entry in entries)
            {
                MelonLogger.Msg("[Coop]   " + entry);
            }
        }
    }
}
