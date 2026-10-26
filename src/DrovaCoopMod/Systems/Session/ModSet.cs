using Drova_Modding_API.Access;
using DrovaCoopMod.Systems.Players;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>Identifies the loaded mods and their content files, so two players notice when they do not run the same ones.</summary>
    internal static class ModSet
    {
        private static uint _hash;
        private static bool _computed;
        private static List<string>? _entries;

        /// <summary>How often a teammate's mod set or content differed from this one. For tests.</summary>
        internal static int Mismatches { get; private set; }

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
                _entries = entries;

                _hash = ActionPrefabIndex.Hash(string.Join("\n", entries));
            }
            catch (Exception e)
            {
                // The far side treats zero as no opinion, not a mismatch.
                _hash = 0u;
                MelonLogger.Warning("[Coop] Could not read the mod list; the mod set will not be compared: " + e.Message);
            }

            return _hash;
        }

        /// <summary>The loaded mods, one line each, and the API's JSON content. Printed locally rather than sent, so no peer text reaches the log.</summary>
        private static List<string> Describe()
        {
            List<string> entries = new();

            foreach (MelonMod mod in MelonMod.RegisteredMelons)
            {
                entries.Add($"{mod.Info.Name} {mod.Info.Version}");
            }

            // Quests, placements, custom items and map layouts derive guids and item ids from these files.
            entries.Add("Modding_API content " + ModContentAccess.GetContentHash());
            return entries;
        }

        /// <summary>Writes the mod list that was hashed to the log after a reported mismatch. The files may have changed since.</summary>
        internal static void Report()
        {
            Mismatches++;
            Hash();
            List<string> entries = _entries ?? [];

            MelonLogger.Msg($"[Coop] This game runs {entries.Count} mods (hash {Hash():X8}):");
            foreach (string entry in entries)
            {
                MelonLogger.Msg("[Coop]   " + entry);
            }
        }
    }
}
