using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova.Saveables;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes a world object that one player changed look changed in every world: a door opened, a lever
    /// thrown, a crate smashed, a gate raised.
    ///
    /// The game keeps this state in three list-shaped components hanging off a save root, and they are
    /// the only three that carry it for authored scene objects. Each rebuilds its list by sweeping the
    /// root for a component interface, so the list is the same length in the same order on both machines
    /// and an index addresses the same object in both. That is what lets the whole list cross as one
    /// value instead of needing a name per object.
    ///
    /// What crosses is the state, never the act. There is no attempt to replay the swing that broke the
    /// crate, because these objects have no charge count to walk down and no actor to attribute: the
    /// game's own load path sets them from a saved value, and that is the path used here. Resource spots
    /// are the exception and are not handled here at all - they carry a count, a tally and an actor
    /// gate, and <see cref="ResourceWork"/> replays them as swings for exactly that reason.
    ///
    /// Host-authoritative in the ordinary way: a client tells the host, the host tells everyone else and
    /// keeps a ledger so a joiner is caught up.
    /// </summary>
    internal static class WorldObjects
    {
        /// <summary>How many arrived changes one frame applies, so a burst cannot stall the world.</summary>
        private const int ApplyBudgetPerFrame = 8;

        /// <summary>
        /// Frames between a component announcing its list and the state being written into it.
        ///
        /// Not a courtesy. A save root builds its saveables and then loads its own saved state into the
        /// very lists this writes to, so applying at the moment the list appears is applying before the
        /// savegame overwrites it. A handful of frames later the load has run either way.
        /// </summary>
        private const int ApplyDelayFrames = 5;

        /// <summary>Frames before an entry that could not be written is looked at again.</summary>
        private const int ApplyRetryFrames = 30;

        /// <summary>
        /// How many attempts an entry gets before it is dropped. An entry that never fits - two mod sets
        /// disagreeing about how many objects a root holds - would otherwise be retried forever and hold
        /// the budget against everything behind it.
        /// </summary>
        private const int ApplyAttempts = 10;

        private static readonly Dictionary<string, Saveable_GenericBoolState> _boolByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Saveable_GenericFloatState> _floatByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Saveable_GameObject_ActivationState> _activationByKey = new(StringComparer.Ordinal);

        /// <summary>Everything the session has seen move, by key and kind. The host's copy is what a joiner gets.</summary>
        private static readonly Dictionary<string, WorldObjectMessage> _ledger = new(StringComparer.Ordinal);

        /// <summary>
        /// Entries whose object has not been reached yet, by the same key, and when each is worth another
        /// attempt, as a reading of <see cref="_frame"/>.
        /// </summary>
        private static readonly Dictionary<string, int> _applyAt = new(StringComparer.Ordinal);

        /// <summary>How many attempts each pending entry has had.</summary>
        private static readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

        private static readonly List<string> _applyNow = new();

        /// <summary>
        /// The key each component was filed under, by instance.
        ///
        /// Decided once, at registration, and reused when the component reports. The root a save root
        /// hands its saveables is its own serialized field and need not be the object the saveable sits
        /// under, so deriving the key again from the component would sometimes answer differently - and a
        /// component filed under one name and announced under another is one nobody can act on.
        /// </summary>
        private static readonly Dictionary<int, string> _keyByComponent = new();

        private static int _frame;

        private static int _snapshotReceived;

        /// <summary>How many of the six patches applied. Six is the number that means the feature works.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How many local changes this machine told the session about.</summary>
        internal static int Sent { get; private set; }

        /// <summary>How many remote changes this machine wrote into its own world.</summary>
        internal static int Applied { get; private set; }

        /// <summary>How many remote changes are still waiting for their object to stream in.</summary>
        internal static int Held => _applyAt.Count;

        /// <summary>How many objects the session has seen move, on this machine's ledger.</summary>
        internal static int Known => _ledger.Count;

        /// <summary>How many objects are loaded here under a key a teammate could name.</summary>
        internal static int Addressable => _boolByKey.Count + _floatByKey.Count + _activationByKey.Count;

        /// <summary>How many entries were given up on because this world's copy never fit them.</summary>
        internal static int Abandoned { get; private set; }

        /// <summary>
        /// Whether the host's join snapshot has arrived in full. A client that is still waiting has a
        /// world that is behind rather than wrong, which is worth being able to tell apart.
        /// </summary>
        internal static bool SnapshotComplete { get; private set; }

        private static bool IsHost => CoopSession.State == CoopSessionState.Hosting;

        /// <summary>
        /// Applies the patches. Called once at melon init.
        ///
        /// Three pairs, one per component: the sweep that builds its list, which is where the object
        /// first has both a root and a known length, and the listener that fires when the state moves.
        /// Every listener target is a delegate the game hands to an event, so none of them can have been
        /// inlined away; the two <c>Start</c> methods are Unity messages, which the engine calls by name.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GenericBoolState), nameof(Saveable_GenericBoolState.OnInitRoot),
                typeof(WorldObjects), nameof(BoolInitPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GenericBoolState), "OnStateChangedEvent",
                typeof(WorldObjects), nameof(BoolChangedPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GenericFloatState), nameof(Saveable_GenericFloatState.OnInitRoot),
                typeof(WorldObjects), nameof(FloatInitPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GenericFloatState), "OnStateChangedEvent",
                typeof(WorldObjects), nameof(FloatChangedPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GameObject_ActivationState), "Start",
                typeof(WorldObjects), nameof(ActivationStartPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GameObject_ActivationState), "OnStateChangedEventListener",
                typeof(WorldObjects), nameof(ActivationChangedPostfix))) applied++;

            HooksInstalled = applied;

            if (applied == 6)
            {
                MelonLogger.Msg("[WorldObjects] 6 of 6 hooks installed; world objects can cross.");
            }
            else
            {
                MelonLogger.Warning($"[WorldObjects] {applied} of 6 hooks installed; world objects will only half cross. " +
                    "coop_status says which counts are moving.");
            }
        }

        /// <summary>Forgets everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            // The three address tables deliberately survive. They are filled when a save root builds its
            // saveables, which happens once per load and not once per session, so clearing them would
            // leave every door and lever already streamed in unaddressable for a second session in the
            // same world - the join snapshot and every live change would park and then be given up on.
            _ledger.Clear();
            _applyAt.Clear();
            _attempts.Clear();
            _applyNow.Clear();
            _keyByComponent.Clear();
            _frame = 0;
            Abandoned = 0;
            Sent = 0;
            Applied = 0;
            _snapshotReceived = 0;
            SnapshotComplete = false;
        }

        /// <summary>
        /// Writes whatever is waiting into the objects that have since loaded. Called once a frame.
        /// </summary>
        internal static void Update()
        {
            _frame++;

            if (_applyAt.Count == 0) return;

            _applyNow.Clear();
            foreach ((string key, int due) in _applyAt)
            {
                if (_frame < due) continue;

                _applyNow.Add(key);
                if (_applyNow.Count >= ApplyBudgetPerFrame) break;
            }

            foreach (string key in _applyNow)
            {
                if (!_ledger.TryGetValue(key, out WorldObjectMessage entry))
                {
                    Forget(key);
                    continue;
                }

                if (TryApply(entry)) continue;

                // Stamped forward rather than left at the head of the queue, so one entry this world
                // cannot fit does not keep every other entry from being tried.
                int attempts = _attempts.GetValueOrDefault(key) + 1;
                _attempts[key] = attempts;

                if (attempts >= ApplyAttempts)
                {
                    Forget(key);
                    Abandoned++;
                    continue;
                }

                _applyAt[key] = _frame + ApplyRetryFrames;
            }

            _applyNow.Clear();
        }

        private static void Forget(string key)
        {
            _applyAt.Remove(key);
            _attempts.Remove(key);
        }

        /// <summary>
        /// Queues a ledger entry for a fresh round of attempts, a few frames out.
        /// </summary>
        private static void Requeue(string key)
        {
            _attempts.Remove(key);
            _applyAt[key] = _frame + ApplyDelayFrames;
        }

        /// <summary>
        /// Takes an entry that arrived over the wire, remembers it, and writes it in when it can.
        /// </summary>
        internal static void Park(in WorldObjectMessage message)
        {
            if (!message.IsReplicable()) return;

            string key = LedgerKey(message.RootKey, message.Kind);
            _ledger[key] = message;

            // Queued rather than written here. A change can arrive while its save root is still building
            // itself, and the load that follows would overwrite whatever was written first.
            Requeue(key);
        }

        /// <summary>Counts a snapshot chunk from the host on the way in.</summary>
        internal static void TakeSnapshot(in WorldObjectSnapshotMessage message)
        {
            WorldObjectSnapshotMessage.Record[] records = message.Records ?? [];

            foreach (WorldObjectSnapshotMessage.Record record in records)
            {
                Park(new WorldObjectMessage
                {
                    RootKey = record.RootKey,
                    Kind = record.Kind,
                    Values = record.Values,
                });

                _snapshotReceived++;
            }
        }

        /// <summary>Notes that the host's snapshot is complete.</summary>
        internal static void CompleteSnapshot(int count)
        {
            SnapshotComplete = true;
            MelonLogger.Msg($"[WorldObjects] join snapshot complete: {_snapshotReceived} of {count} entries.");
        }

        /// <summary>Everything the session has seen move, for the savegame to remember.</summary>
        internal static IEnumerable<WorldObjectSnapshotMessage.Record> ExportLedger()
        {
            foreach (KeyValuePair<string, WorldObjectMessage> entry in _ledger)
            {
                yield return new WorldObjectSnapshotMessage.Record
                {
                    RootKey = entry.Value.RootKey,
                    Kind = entry.Value.Kind,
                    Values = entry.Value.Values,
                };
            }
        }

        /// <summary>
        /// Puts a world object this world changed in an earlier session back on the ledger, so a joiner
        /// is told about it and this world's own copy is set if it has drifted.
        /// </summary>
        /// <returns>False when the entry names nothing a teammate could act on.</returns>
        internal static bool ImportLedger(string rootKey, byte kind, float[] values)
        {
            WorldObjectMessage entry = new()
            {
                RootKey = rootKey,
                Kind = kind,
                Values = values,
            };

            if (!entry.IsReplicable()) return false;

            string key = LedgerKey(rootKey, kind);
            if (_ledger.ContainsKey(key)) return false;

            _ledger[key] = entry;
            Requeue(key);
            return true;
        }

        /// <summary>Host only: sends the whole ledger to one joiner, in chunks.</summary>
        internal static void SendTo(Drova_Modding_API.Systems.Networking.INetPeer peer)
        {
            foreach (WorldObjectSnapshotMessage.Record[] chunk in ExportLedger().Chunk(WorldObjectSnapshotMessage.MaxRecords))
            {
                CoopSession.SendWorldObjectSnapshot(peer, chunk);
            }

            int total = _ledger.Count;

            CoopSession.SendWorldObjectSnapshotEnd(peer, (ushort)Math.Min(total, ushort.MaxValue));
        }

        private static void BoolInitPostfix(Saveable_GenericBoolState __instance)
        {
            Register(__instance, _boolByKey);
        }

        private static void FloatInitPostfix(Saveable_GenericFloatState __instance)
        {
            Register(__instance, _floatByKey);
        }

        private static void ActivationStartPostfix(Saveable_GameObject_ActivationState __instance)
        {
            Register(__instance, _activationByKey);
        }

        // The loading check is not decoration. Restoring a savegame sets these objects, and setting them
        // fires the very listener this watches, so without it a client finishing its own load would
        // broadcast its own saved doors over the host's. The components guard their own writes the same
        // way; a postfix runs whether or not they did.
        private static void BoolChangedPostfix(Saveable_GenericBoolState __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            Broadcast(__instance, WorldObjectMessage.KindBool, _boolByKey, ReadBool);
        }

        private static void FloatChangedPostfix(Saveable_GenericFloatState __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            Broadcast(__instance, WorldObjectMessage.KindFloat, _floatByKey, ReadFloat);
        }

        private static void ActivationChangedPostfix(Saveable_GameObject_ActivationState __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            Broadcast(__instance, WorldObjectMessage.KindActivation, _activationByKey, ReadActivation);
        }

        /// <summary>
        /// Files a component under its save root's key, and writes in anything that was waiting for it.
        /// </summary>
        private static void Register<T>(T saveable, Dictionary<string, T> table) where T : Component
        {
            try
            {
                // From the component itself, not from the root handed in: the two can differ, and the
                // component is what reports later.
                string? key = KeyOf(saveable.transform);
                if (key == null) return;

                table[key] = saveable;
                _keyByComponent[saveable.GetInstanceID()] = key;

                // A change can easily arrive before the chunk holding its object does, and the ledger keeps
                // it. Queued a few frames out rather than written now: this runs from the sweep that
                // builds a save root's saveables, and the root loads its own saved state into those same
                // lists immediately afterwards.
                foreach (byte kind in Kinds)
                {
                    string ledgerKey = LedgerKey(key, kind);
                    if (!_ledger.ContainsKey(ledgerKey)) continue;

                    Requeue(ledgerKey);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[WorldObjects] registering a world object failed: " + e);
            }
        }

        private static readonly byte[] Kinds =
        [
            WorldObjectMessage.KindBool,
            WorldObjectMessage.KindFloat,
            WorldObjectMessage.KindActivation,
        ];

        /// <summary>
        /// Tells the session what a component now holds, unless this change is one that arrived over the
        /// wire a moment ago.
        /// </summary>
        private static void Broadcast<T>(T saveable, byte kind, Dictionary<string, T> table, Func<T, float[]?> read)
            where T : Component
        {
            if (RemoteApply<WorldObjectScope>.InProgress) return;
            if (!CoopSession.IsLive) return;

            try
            {
                // The name it was filed under, so a change is announced under the name teammates know.
                if (!_keyByComponent.TryGetValue(saveable.GetInstanceID(), out string? key)) return;

                float[]? values = read(saveable);

                // A list longer than the wire carries is left alone rather than cut. Cutting would put
                // the two worlds' indices out of step, and every entry past the cut would then be
                // written onto the wrong object.
                if (values == null || values.Length > WorldObjectMessage.MaxValues) return;

                WorldObjectMessage message = new()
                {
                    RootKey = key,
                    Kind = kind,
                    Values = values,
                };

                string ledgerKey = LedgerKey(key, kind);

                // Unchanged since the last time it was reported. These listeners fire on every write the
                // component makes, including the ones that rewrite the same value.
                if (_ledger.TryGetValue(ledgerKey, out WorldObjectMessage known) && Same(known.Values, values)) return;

                _ledger[ledgerKey] = message;
                CoopSession.ReportWorldObject(message);
                Sent++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[WorldObjects] reporting a world object failed: " + e);
            }
        }

        /// <summary>
        /// Writes an entry into this world's copy, if the copy is here and the shapes agree.
        /// </summary>
        /// <returns>False when this world's copy is not here yet, or does not fit the entry.</returns>
        private static bool TryApply(in WorldObjectMessage message)
        {
            string key = LedgerKey(message.RootKey, message.Kind);

            try
            {
                using (RemoteApply<WorldObjectScope>.Enter())
                {
                    switch (message.Kind)
                    {
                        case WorldObjectMessage.KindBool:
                            if (!_boolByKey.TryGetValue(message.RootKey, out Saveable_GenericBoolState? bools) || bools == null) return false;
                            if (!ApplyBool(bools, message.Values)) return false;
                            break;

                        case WorldObjectMessage.KindFloat:
                            if (!_floatByKey.TryGetValue(message.RootKey, out Saveable_GenericFloatState? floats) || floats == null) return false;
                            if (!ApplyFloat(floats, message.Values)) return false;
                            break;

                        default:
                            if (!_activationByKey.TryGetValue(message.RootKey, out Saveable_GameObject_ActivationState? activation) || activation == null) return false;
                            if (!ApplyActivation(activation, message.Values)) return false;
                            break;
                    }
                }

                Forget(key);
                Applied++;
                return true;
            }
            catch (Exception e)
            {
                Forget(key);
                MelonLogger.Error("[WorldObjects] applying a world object failed: " + e);
                return true;
            }
        }

        private static bool ApplyBool(Saveable_GenericBoolState saveable, float[] values)
        {
            Il2CppSystem.Collections.Generic.List<Il2CppDrova.Saveables.ISaveableBool> list = saveable._spriteChangeBhvrs;
            if (list == null || list.Count != values.Length) return false;

            for (int index = 0; index < list.Count; index++)
            {
                list[index].OnLoadTrigger(values[index] != 0f);
            }

            WriteBoolSaveData(saveable, values);
            return true;
        }

        /// <summary>
        /// Puts the applied state where the next save will find it.
        ///
        /// The component only ever fills its saved value from the change event, and its save callback is
        /// empty; an implementation whose load trigger just assigns a field raises nothing, so the value
        /// would never be recorded. On a real load the component does not need this because the value
        /// came out of the file in the first place - here it did not.
        /// </summary>
        private static void WriteBoolSaveData(Saveable_GenericBoolState saveable, float[] values)
        {
            SaveData_BoolList? data = saveable._saveData;
            if (data == null || data.Value == null || data.Value.Length != values.Length) return;

            for (int index = 0; index < values.Length; index++)
            {
                data.Value[index] = values[index] != 0f;
            }
        }

        private static bool ApplyFloat(Saveable_GenericFloatState saveable, float[] values)
        {
            Il2CppSystem.Collections.Generic.List<Il2CppDrova.Saveables.ISaveableFloat> list = saveable._saveBhvrs;
            if (list == null || list.Count != values.Length) return false;

            for (int index = 0; index < list.Count; index++)
            {
                list[index].OnLoadTrigger(values[index]);
            }

            // Same reason as the bool list: the saved value is only ever written from the change event.
            SaveData_FloatList? data = saveable._saveData;
            if (data != null && data.Value != null && data.Value.Length == values.Length)
            {
                for (int index = 0; index < values.Length; index++)
                {
                    data.Value[index] = values[index];
                }
            }

            return true;
        }

        private static bool ApplyActivation(Saveable_GameObject_ActivationState saveable, float[] values)
        {
            Il2CppSystem.Collections.Generic.List<GameObject> list = saveable._gameObject;
            if (list == null || list.Count != values.Length) return false;

            for (int index = 0; index < list.Count; index++)
            {
                GameObject target = list[index];
                if (target == null) continue;

                target.SetActive(values[index] != 0f);
            }

            return true;
        }

        private static float[]? ReadBool(Saveable_GenericBoolState saveable)
        {
            Il2CppSystem.Collections.Generic.List<Il2CppDrova.Saveables.ISaveableBool> list = saveable._spriteChangeBhvrs;
            if (list == null) return null;

            float[] values = new float[list.Count];
            for (int index = 0; index < list.Count; index++)
            {
                values[index] = list[index].State ? 1f : 0f;
            }

            return values;
        }

        private static float[]? ReadFloat(Saveable_GenericFloatState saveable)
        {
            Il2CppSystem.Collections.Generic.List<Il2CppDrova.Saveables.ISaveableFloat> list = saveable._saveBhvrs;
            if (list == null) return null;

            float[] values = new float[list.Count];
            for (int index = 0; index < list.Count; index++)
            {
                values[index] = list[index].SaveableState;
            }

            return values;
        }

        private static float[]? ReadActivation(Saveable_GameObject_ActivationState saveable)
        {
            Il2CppSystem.Collections.Generic.List<GameObject> list = saveable._gameObject;
            if (list == null) return null;

            float[] values = new float[list.Count];
            for (int index = 0; index < list.Count; index++)
            {
                GameObject target = list[index];
                values[index] = target != null && target.activeSelf ? 1f : 0f;
            }

            return values;
        }

        private static bool Same(float[]? left, float[]? right)
        {
            if (left == null || right == null) return false;

            return left.AsSpan().SequenceEqual(right);
        }

        /// <summary>
        /// One root can carry more than one of the three lists, so the ledger is keyed by both.
        /// </summary>
        private static string LedgerKey(string rootKey, byte kind)
        {
            return rootKey + "|" + kind;
        }

        /// <summary>
        /// The save root key of a scene object, or null when no teammate could name it: nothing above it
        /// saves itself, or it saves under a root numbered per savegame. Shared by every world subsystem
        /// that files a scene object by its save root.
        /// </summary>
        internal static string? KeyOf(Component component)
        {
            ASaveRoot? root = component.GetComponent<ASaveRoot>();
            if (root == null) root = component.GetComponentInParent<ASaveRoot>();
            if (root == null) return null;

            SaveRoot_Dynamic? dynamic = root.TryCast<SaveRoot_Dynamic>();
            if (dynamic != null && dynamic.IsDynamic()) return null;

            string key = root.GetGameObjectInstanceKey();
            return ResourceWorkMessage.IsReplicableKey(key) ? key : null;
        }
    }
}
