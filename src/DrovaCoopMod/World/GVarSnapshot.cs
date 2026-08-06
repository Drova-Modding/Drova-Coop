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

                if (sent > 0)
                {
                    MelonLogger.Msg($"[GVar] a joining player was sent {sent} variable(s) the world had " +
                        "already changed, so their quest log starts where this world stands.");
                }
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

            _records.Clear();

            string listName = list.name;
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

            AGVar<float>? asFloat = gvar.TryCast<AGVar<float>>();
            if (asFloat != null)
            {
                float value = asFloat.GetValue();
                if (value == asFloat.GetInitialValue()) return false;

                record = Build(gvar, GVarChangeMessage.KindFloat, 0, value);
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

            return false;
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
