using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.QuestSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Sends a joining player everything the world's variables have already become.
    ///
    /// Live changes only reach machines that are connected when they happen. What crosses here is every
    /// variable whose value differs from its authored initial, which is exactly the set a fresh client
    /// could not know: quest progress, dialogue consequences, world flags. The client applies it through
    /// the same path a live change takes, so its quest log catches up the moment it joins.
    ///
    /// Variables a mod declared local stay home, the same rule live changes follow.
    /// </summary>
    internal static class GVarSnapshot
    {
        private static readonly List<GVarSnapshotMessage.Record> _records = new();

        /// <summary>
        /// Sends the moved variables of every list to one peer. Called by the host when a player joins.
        /// </summary>
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

                // The terminator, always, even for a world that has moved nothing. The receiver stores
                // every value before running any of their consequences, and that split needs a real
                // end rather than a guess at one - the build before this inferred it from thirty quiet
                // frames, which cannot tell an empty queue from an exhausted per-frame budget.
                NetworkAccess.Send(peer, new GVarSnapshotEndMessage { RecordCount = (uint)sent });

                MelonLogger.Msg($"[GVar] a joining player was sent {sent} variable(s) the world had " +
                    "already changed, so their quest log starts where this world stands.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] building the join snapshot failed: " + e);
            }
        }

        /// <summary>
        /// Sends one list's moved variables, in chunks.
        /// </summary>
        /// <returns>How many variables crossed.</returns>
        private static int SendList(INetPeer peer, GVarList list)
        {
            Il2CppSystem.Collections.Generic.List<AGVarBase> vars = list.GetGVars();
            if (vars == null) return 0;

            string listName = list.name;

            // Every list. No exception for the lists a quest actually lives in, which is what the
            // build before this one did - and it was the single worst line in the mod. A quest arc
            // keeps its own quest-state variable in the same list as its stage flags, so excluding
            // "story sequence" lists from the catch-up excluded the one variable that says the quest
            // is running at all. A player who joined mid-arc then received nothing of it, ever, and
            // there was no second path that could have repaired that.
            //
            // What made the exclusion look necessary was that a catch-up used to be applied like a
            // live change, so writing a story gate into a joining world re-staged it there. That is
            // no longer how a catch-up lands: values are stored first and their consequences run
            // afterwards, with the observers that act on this player's body held shut. State can
            // therefore cross without the story happening to the person receiving it.

            _records.Clear();

            int sent = 0;

            for (int index = 0; index < vars.Count; index++)
            {
                AGVarBase gvar = vars[index];
                if (gvar == null || string.IsNullOrEmpty(gvar.Id)) continue;

                if (GVarSyncAccess.IsLocal(listName, gvar.name)) continue;

                if (!TryBuildRecord(gvar, out GVarSnapshotMessage.Record record)) continue;

                _records.Add(record);

                if (_records.Count >= GVarSnapshotMessage.MaxRecords)
                {
                    sent += Flush(peer, list.Guid);
                }
            }

            sent += Flush(peer, list.Guid);
            return sent;
        }

        private static int Flush(INetPeer peer, string listGuid)
        {
            if (_records.Count == 0) return 0;

            NetworkAccess.Send(peer, new GVarSnapshotMessage
            {
                ListGuid = listGuid,
                Records = [.. _records],
            });

            int sent = _records.Count;
            _records.Clear();
            return sent;
        }

        /// <summary>
        /// Builds the record for a variable that has moved from its initial value.
        /// </summary>
        /// <returns>False when it has not moved, or is none of the four kinds that cross.</returns>
        private static bool TryBuildRecord(AGVarBase gvar, out GVarSnapshotMessage.Record record)
        {
            record = default;

            AGVar<bool>? asBool = gvar.TryCast<AGVar<bool>>();
            if (asBool != null)
            {
                bool value = asBool.GetValue();
                if (value == asBool.GetInitialValue()) return false;

                record = Build(gvar, GVarChangeMessage.KindBool, value ? 1 : 0, 0f);
                return true;
            }

            AGVar<int>? asInt = gvar.TryCast<AGVar<int>>();
            if (asInt != null)
            {
                int value = asInt.GetValue();
                if (value == asInt.GetInitialValue()) return false;

                record = Build(gvar, GVarChangeMessage.KindInt, value, 0f);
                return true;
            }
            
            AGVar<QuestState>? asQuest = gvar.TryCast<AGVar<QuestState>>();
            if (asQuest != null)
            {
                QuestState value = asQuest.GetValue();
                if (value == asQuest.GetInitialValue()) return false;

                record = Build(gvar, GVarChangeMessage.KindQuest, (int)value, 0f);
                return true;
            }

            AGVar<float>? asFloat = gvar.TryCast<AGVar<float>>();
            if (asFloat != null)
            {
                float value = asFloat.GetValue();
                if (value == asFloat.GetInitialValue()) return false;

                record = Build(gvar, GVarChangeMessage.KindFloat, 0, value);
                return true;
            }
            
            return false;
        }

        /// <summary>How many variables one report lists before it only counts.</summary>
        private const int MaxListed = 60;

        /// <summary>
        /// Says every variable that differs from its authored initial, by name. This is how the
        /// variables behind a story gate are found, so they can be declared journey-shared.
        /// </summary>
        /// <param name="filter">Substring to narrow by, matched against "List.Var". Empty lists all.</param>
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
                    say("  " + name + " = " + Describe(record));
                }
            }

            say($"{matched} moved variable(s)" +
                (string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'") +
                (matched > listed ? $", {matched - listed} not listed." : "."));
        }

        private static string Describe(in GVarSnapshotMessage.Record record)
        {
            return record.Kind switch
            {
                GVarChangeMessage.KindBool => record.IntValue != 0 ? "true" : "false",
                GVarChangeMessage.KindFloat => record.FloatValue.ToString("0.###"),
                GVarChangeMessage.KindQuest => "quest state " + record.IntValue,
                _ => record.IntValue.ToString(),
            };
        }

        private static GVarSnapshotMessage.Record Build(AGVarBase gvar, byte kind, int intValue, float floatValue)
        {
            return new GVarSnapshotMessage.Record
            {
                VarId = gvar.Id,
                Kind = kind,
                IntValue = intValue,
                FloatValue = floatValue,
            };
        }
    }
}
