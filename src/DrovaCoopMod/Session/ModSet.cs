using MelonLoader;

namespace DrovaCoopMod.Session
{
    /// <summary>Identifies the loaded mods, so two players notice when they do not run the same ones.</summary>
    internal static class ModSet
    {
        private static uint _hash;
        private static bool _computed;

        /// <summary>A hash of the loaded mods and their versions, stable across runs and machines. Zero means unknown.</summary>
        internal static uint Hash()
        {
            if (_computed) return _hash;

            _computed = true;

            try
            {
                // Sorted, because load order differs between machines.
                List<string> entries = Describe();
                entries.Sort(StringComparer.Ordinal);

                _hash = World.ActionPrefabIndex.Hash(string.Join("\n", entries));
            }
            catch (Exception e)
            {
                // The far side treats zero as no opinion, not a mismatch.
                _hash = 0u;
                MelonLogger.Warning("[Coop] Could not read the mod list; the mod set will not be compared: " + e.Message);
            }

            return _hash;
        }

        /// <summary>The loaded mods, one line each. Printed locally rather than sent, so no peer text reaches the log.</summary>
        internal static List<string> Describe()
        {
            List<string> entries = new();

            foreach (MelonMod mod in MelonMod.RegisteredMelons)
            {
                entries.Add($"{mod.Info.Name} {mod.Info.Version}");
            }

            return entries;
        }

        /// <summary>Writes the local mod list to the log after a reported mismatch.</summary>
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
