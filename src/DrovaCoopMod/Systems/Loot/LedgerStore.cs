using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppDrova.Saveables;
using MelonLoader;

namespace DrovaCoopMod.Systems.Loot
{
    /// <summary>
    /// Keeps the taken-pickup and world-object ledgers in the host's savegame, since nothing else settles those across a restart.
    /// Written from the save funnel because the autosave never raises the API's saving event.
    /// </summary>
    internal sealed class LedgerStore : ICoopSystem
    {
        private const string PickupKey = "DrovaCoop.Ledger.Pickups";

        private const string ObjectKey = "DrovaCoop.Ledger.Objects";

        /// <summary>The most entries either ledger saves. Past it the oldest are kept.</summary>
        private const int MaxEntries = 4000;

        /// <summary>The savegame the ledger was read from, since a session started in the menu sees a different one later.</summary>
        private static IntPtr _loadedFrom;

        private static int _pickupsWritten;

        private static int _objectsWritten;

        /// <summary>How many pickups came out of this savegame. For <c>coop_status</c>.</summary>
        internal static int PickupsRead { get; private set; }

        /// <summary>How many world objects came out of this savegame.</summary>
        internal static int ObjectsRead { get; private set; }

        /// <summary>How many entries went into it at the last save.</summary>
        internal static int Written => _pickupsWritten + _objectsWritten;

        /// <summary>Whether this savegame's ledger has been read into the session yet.</summary>
        internal static bool Loaded => _loadedFrom != IntPtr.Zero;

        /// <summary>Forgets that the savegame was read, so the next session reads it again.</summary>
        private static void Reset()
        {
            _loadedFrom = IntPtr.Zero;
            PickupsRead = 0;
            ObjectsRead = 0;
        }

        /// <summary>Reads this world's ledgers into the session. Harmless to call again.</summary>
        private static void Load()
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            try
            {
                Savegame? save = Savegame.Current;
                SavegameData? data = save?.Data;

                // No world yet when started from the menu. Tried again next frame.
                if (save == null || data == null) return;
                if (_loadedFrom == save.Pointer) return;

                PickupsRead = 0;
                ObjectsRead = 0;

                string pickups = string.Empty;
                if (data.GetString(PickupKey, ref pickups))
                {
                    foreach (string line in Split(pickups))
                    {
                        if (!LootShare.ImportTakenPickup(line)) continue;

                        PickupsRead++;
                    }
                }

                string objects = string.Empty;
                if (data.GetString(ObjectKey, ref objects))
                {
                    foreach (string line in Split(objects))
                    {
                        if (!ImportObject(line)) continue;

                        ObjectsRead++;
                    }
                }

                _loadedFrom = save.Pointer;

                if (PickupsRead > 0 || ObjectsRead > 0)
                {
                    MelonLogger.Msg($"[Ledger] this world remembers {PickupsRead} pickup(s) and " +
                        $"{ObjectsRead} world object(s) from earlier sessions.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Ledger] reading this world's shared ledger failed: " + e);
            }
        }

        /// <summary>Writes the session's ledgers into the savegame about to be saved. Host only.</summary>
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
                // Never let this stop a save.
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

            // Read back, since the savegame silently drops writes while the game is not accepting them.
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

        private static string[] Split(string? value)
        {
            return (value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            // Every frame, since a session can start in the main menu before a savegame exists. Cheap once loaded.
            Load();
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
