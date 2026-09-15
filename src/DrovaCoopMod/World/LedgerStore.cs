using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps the party's shared-world ledger in the host's savegame, so a world remembers across a
    /// restart what the party did to it.
    ///
    /// The ledgers that make a joining player's world match everybody else's live in memory and die with
    /// the session. That is fine for anything the two saves settle between them - a chest is offered by
    /// both machines when it streams in and the host's copy outranks, so an emptied chest empties again
    /// on its own - and it is not fine for the two cases nothing settles: a scene pickup, which is simply
    /// gone in one save and present in the other, and a world object, which has no offer at all. Without
    /// this, a pickup the host took last week is taken again by the client this week, and a door the
    /// party opened is shut in every world but the host's.
    ///
    /// Host only, and one string per ledger rather than a key per entry: the join snapshot needs the
    /// whole list at once, and a savegame cannot be enumerated for keys that share a prefix.
    ///
    /// Written from the same save funnel the backups use, because the autosave never raises the modding
    /// API's own saving event. Read lazily rather than on a load event, for the same reason: the one
    /// moment this is needed is the first time a session wants it, and by then the savegame is up.
    /// </summary>
    internal static class LedgerStore
    {
        /// <summary>Where the pickups the party has taken are kept.</summary>
        private const string PickupKey = "DrovaCoop.Ledger.Pickups";

        /// <summary>Where the world objects the party has changed are kept.</summary>
        private const string ObjectKey = "DrovaCoop.Ledger.Objects";

        /// <summary>
        /// The most entries either ledger carries into a savegame.
        ///
        /// A bound rather than a belief. Both grow for the life of a world and a savegame string is read
        /// and written whole; a party that has emptied ten thousand pickups is better served by a ledger
        /// that stopped growing than by a save that takes a second to write. Past it the oldest are kept
        /// and the newest dropped, because an entry already in the savegame has already been relied on.
        /// </summary>
        private const int MaxEntries = 4000;

        /// <summary>
        /// The savegame the ledger was read out of, by native address.
        ///
        /// Not a flag. A session can be started from the main menu, where there is no savegame at all
        /// yet, and the one that is current a moment later is a different object - reading the old one
        /// would import another world's ledger and then write it back over this one.
        /// </summary>
        private static IntPtr _loadedFrom;

        private static int _pickupsRead;

        private static int _objectsRead;

        private static int _pickupsWritten;

        private static int _objectsWritten;

        /// <summary>How many pickups came out of this savegame. For <c>coop_status</c>.</summary>
        internal static int PickupsRead => _pickupsRead;

        /// <summary>How many world objects came out of this savegame.</summary>
        internal static int ObjectsRead => _objectsRead;

        /// <summary>How many entries went into it at the last save.</summary>
        internal static int Written => _pickupsWritten + _objectsWritten;

        /// <summary>Whether this savegame's ledger has been read into the session yet.</summary>
        internal static bool Loaded => _loadedFrom != IntPtr.Zero;

        /// <summary>Forgets that the savegame was read, so the next session reads it again.</summary>
        internal static void Reset()
        {
            _loadedFrom = IntPtr.Zero;
            _pickupsRead = 0;
            _objectsRead = 0;
        }

        /// <summary>
        /// Reads what this world already knows into the session's ledgers. Called when a host session
        /// starts, and harmless to call again.
        /// </summary>
        internal static void Load()
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            try
            {
                Savegame? save = Savegame.Current;
                SavegameData? data = save?.Data;

                // Nothing to read yet, which is the ordinary answer while a session started from the main
                // menu waits for a world. Tried again next frame rather than given up on.
                if (save == null || data == null) return;
                if (_loadedFrom == save.Pointer) return;

                // Read before it is believed, so a throw halfway does not latch a half-imported ledger.
                _pickupsRead = 0;
                _objectsRead = 0;

                string pickups = string.Empty;
                if (data.GetString(PickupKey, ref pickups))
                {
                    foreach (string line in Split(pickups))
                    {
                        if (!LootShare.ImportTakenPickup(line)) continue;

                        _pickupsRead++;
                    }
                }

                string objects = string.Empty;
                if (data.GetString(ObjectKey, ref objects))
                {
                    foreach (string line in Split(objects))
                    {
                        if (!ImportObject(line)) continue;

                        _objectsRead++;
                    }
                }

                _loadedFrom = save.Pointer;

                if (_pickupsRead > 0 || _objectsRead > 0)
                {
                    MelonLogger.Msg($"[Ledger] this world remembers {_pickupsRead} pickup(s) and " +
                        $"{_objectsRead} world object(s) from earlier sessions.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Ledger] reading this world's shared ledger failed: " + e);
            }
        }

        /// <summary>
        /// Writes the session's ledgers into the savegame the game is about to save. Called from the save
        /// funnel, host only: a client's savegame is not the world and has no business carrying it.
        /// </summary>
        internal static void Save()
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            try
            {
                SavegameData? data = Savegame.Current?.Data;
                if (data == null) return;

                _pickupsWritten = Write(data, PickupKey, LootShare.ExportTakenPickups());
                _objectsWritten = Write(data, ObjectKey, ExportObjects());
            }
            catch (Exception e)
            {
                // Never let this stop a save. A world that forgot what the party did is recoverable; a
                // save that did not happen is not.
                MelonLogger.Error("[Ledger] writing this world's shared ledger failed: " + e);
            }
        }

        private static int Write(SavegameData data, string key, IEnumerable<string> entries)
        {
            List<string> lines = new();

            foreach (string entry in entries)
            {
                if (string.IsNullOrEmpty(entry) || entry.IndexOf('\n') >= 0) continue;

                lines.Add(entry);
                if (lines.Count >= MaxEntries) break;
            }

            string value = string.Join("\n", lines);
            data.SetString(key, value);

            // Read back rather than trust the call, the way the dialogue ledger does: the savegame drops
            // a write outright while the game is not accepting them, and says nothing about it.
            string check = string.Empty;
            if (!data.GetString(key, ref check) || check != value)
            {
                MelonLogger.Warning($"[Ledger] {key} did not take; this world will not remember this session.");
                return 0;
            }

            return lines.Count;
        }

        private static IEnumerable<string> ExportObjects()
        {
            foreach (WorldObjectSnapshotMessage.Record record in WorldObjects.ExportLedger())
            {
                yield return record.RootKey + "|" + record.Kind + "|" + string.Join(",",
                    (record.Values ?? []).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        private static bool ImportObject(string line)
        {
            string[] parts = line.Split('|');
            if (parts.Length != 3) return false;

            if (!byte.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out byte kind)) return false;

            string[] raw = parts[2].Length == 0 ? [] : parts[2].Split(',');
            float[] values = new float[raw.Length];

            for (int index = 0; index < raw.Length; index++)
            {
                if (!float.TryParse(raw[index], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out values[index])) return false;
            }

            return WorldObjects.ImportLedger(parts[0], kind, values);
        }

        private static IEnumerable<string> Split(string value)
        {
            if (string.IsNullOrEmpty(value)) yield break;

            foreach (string line in value.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) yield return trimmed;
            }
        }
    }
}
