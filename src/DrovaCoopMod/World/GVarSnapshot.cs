using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.QuestSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Sends a joining player every global variable that differs from its authored initial value.
    /// </summary>
    internal static class GVarSnapshot
    {
        // How many variables one report lists before it only counts.
        private const int MaxListed = 60;

        /// <summary>Sends the moved variables of every list to one peer. Called by the host when a player joins.</summary>
        internal static void SendTo(INetPeer peer)
        {
            try
            {
                SubDatabase_GVars database = ProviderAccess.GVarDatabase;
                if (database == null) return;

                Il2CppSystem.Collections.Generic.List<GVarList> lists = database.AllGVars;
                if (lists == null) return;

                int sent = 0;

                for (int listIndex = 0; listIndex < lists.Count; listIndex++)
                {
                    GVarList list = lists[listIndex];
                    if (list == null || string.IsNullOrEmpty(list.Guid)) continue;

                    sent += SendList(peer, list);
                }

                // Always sent, even when empty. The receiver stores every value before running consequences and needs a real end.
                NetworkAccess.Send(peer, new GVarSnapshotEndMessage { RecordCount = (uint)sent });

                MelonLogger.Msg($"[GVar] a joining player was sent {sent} variable(s) the world had " +
                    "already changed, so their quest log starts where this world stands.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] building the join snapshot failed: " + e);
            }
        }

        /// <returns>How many variables crossed.</returns>
        private static int SendList(INetPeer peer, GVarList list)
        {
            Il2CppSystem.Collections.Generic.List<AGVarBase> vars = list.GetGVars();
            if (vars == null) return 0;

            string listName = list.name;

            // Every list, story ones included: a quest's state variable shares a list with its stage flags.

            List<GVarSnapshotMessage.Record> records = new();

            for (int index = 0; index < vars.Count; index++)
            {
                AGVarBase gvar = vars[index];
                if (gvar == null || string.IsNullOrEmpty(gvar.Id)) continue;

                if (GVarSyncAccess.IsLocal(listName, gvar.name)) continue;

                // A work tally. The joiner recounts it from the spot table it receives.
                if (GVarDerived.IsDerived(list.Guid, gvar.Id)) continue;

                if (!TryBuildRecord(gvar, out GVarSnapshotMessage.Record record)) continue;

                records.Add(record);
            }

            foreach (GVarSnapshotMessage.Record[] chunk in records.Chunk(GVarSnapshotMessage.MaxRecords))
            {
                NetworkAccess.Send(peer, new GVarSnapshotMessage { ListGuid = list.Guid, Records = chunk });
            }

            return records.Count;
        }

        /// <returns>False when the variable has not moved, or is none of the four kinds that cross.</returns>
        private static bool TryBuildRecord(AGVarBase gvar, out GVarSnapshotMessage.Record record)
        {
            record = default;

            if (!GVarEvents.TryRead(gvar, out object value, out object initial)) return false;
            if (value.Equals(initial)) return false;
            if (!GVarEvents.TryDescribe(value, out byte kind, out int intValue, out float floatValue)) return false;

            record = new GVarSnapshotMessage.Record
            {
                VarId = gvar.Id,
                Kind = kind,
                IntValue = intValue,
                FloatValue = floatValue,
            };
            return true;
        }

        /// <summary>Lists every variable that differs from its authored initial, by name.</summary>
        /// <param name="filter">Substring matched against "List.Var". Empty lists all.</param>
        /// <param name="say">Where the lines go.</param>
        internal static void Report(string filter, Action<string> say)
        {
            SubDatabase_GVars database = ProviderAccess.GVarDatabase;
            if (database == null || database.AllGVars == null)
            {
                say("The world is not ready.");
                return;
            }

            Il2CppSystem.Collections.Generic.List<GVarList> lists = database.AllGVars;

            int matched = 0;
            int listed = 0;

            for (int listIndex = 0; listIndex < lists.Count; listIndex++)
            {
                GVarList list = lists[listIndex];
                if (list == null) continue;

                Il2CppSystem.Collections.Generic.List<AGVarBase> vars = list.GetGVars();
                if (vars == null) continue;

                for (int index = 0; index < vars.Count; index++)
                {
                    AGVarBase gvar = vars[index];
                    if (gvar == null) continue;

                    if (!TryBuildRecord(gvar, out GVarSnapshotMessage.Record record)) continue;

                    string name = list.name + "." + gvar.name;

                    if (!string.IsNullOrEmpty(filter)
                        && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    matched++;
                    if (listed >= MaxListed) continue;

                    listed++;
                    say("  " + name + " = " + GVarChangeMessage.Describe(record.Kind, record.IntValue, record.FloatValue));
                }
            }

            say($"{matched} moved variable(s)" +
                (string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'") +
                (matched > listed ? $", {matched - listed} not listed." : "."));
        }
    }
}
