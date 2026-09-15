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

        private static readonly Dictionary<string, Saveable_GenericBoolState> _boolByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Saveable_GenericFloatState> _floatByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Saveable_GameObject_ActivationState> _activationByKey = new(StringComparer.Ordinal);

        /// <summary>Everything the session has seen move, by key and kind. The host's copy is what a joiner gets.</summary>
        private static readonly Dictionary<string, WorldObjectMessage> _ledger = new(StringComparer.Ordinal);

        /// <summary>Entries whose object has not been reached yet, by the same key.</summary>
        private static readonly HashSet<string> _pending = new(StringComparer.Ordinal);

        private static readonly List<string> _applyNow = new();

        private static int _hooksInstalled;

        private static int _sent;

        private static int _applied;

        private static int _snapshotReceived;

        private static bool _snapshotComplete;

        /// <summary>How many of the six patches applied. Six is the number that means the feature works.</summary>
        internal static int HooksInstalled => _hooksInstalled;

        /// <summary>How many local changes this machine told the session about.</summary>
        internal static int Sent => _sent;

        /// <summary>How many remote changes this machine wrote into its own world.</summary>
        internal static int Applied => _applied;

        /// <summary>How many remote changes are still waiting for their object to stream in.</summary>
        internal static int Held => _pending.Count;

        /// <summary>How many objects the session has seen move, on this machine's ledger.</summary>
        internal static int Known => _ledger.Count;

        /// <summary>How many objects are loaded here under a key a teammate could name.</summary>
        internal static int Addressable => _boolByKey.Count + _floatByKey.Count + _activationByKey.Count;

        /// <summary>
        /// Whether the host's join snapshot has arrived in full. A client that is still waiting has a
        /// world that is behind rather than wrong, which is worth being able to tell apart.
        /// </summary>
        internal static bool SnapshotComplete => _snapshotComplete;

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

            _hooksInstalled = applied;

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
            _boolByKey.Clear();
            _floatByKey.Clear();
            _activationByKey.Clear();
            _ledger.Clear();
            _pending.Clear();
            _applyNow.Clear();
            _sent = 0;
            _applied = 0;
            _snapshotReceived = 0;
            _snapshotComplete = false;
        }

        /// <summary>
        /// Writes whatever is waiting into the objects that have since loaded. Called once a frame.
        /// </summary>
        internal static void Update()
        {
            if (_pending.Count == 0) return;

            _applyNow.Clear();
            foreach (string key in _pending)
            {
                _applyNow.Add(key);
                if (_applyNow.Count >= ApplyBudgetPerFrame) break;
            }

            foreach (string key in _applyNow)
            {
                if (!_ledger.TryGetValue(key, out WorldObjectMessage entry))
                {
                    _pending.Remove(key);
                    continue;
                }

                TryApply(entry);
            }

            _applyNow.Clear();
        }

        /// <summary>
        /// Takes an entry that arrived over the wire, remembers it, and writes it in when it can.
        /// </summary>
        internal static void Park(in WorldObjectMessage message)
        {
            if (!message.IsReplicable()) return;

            string key = LedgerKey(message.RootKey, message.Kind);
            _ledger[key] = message;
            _pending.Add(key);

            TryApply(message);
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
            _snapshotComplete = true;
            MelonLogger.Msg($"[WorldObjects] join snapshot complete: {_snapshotReceived} of {count} entries.");
        }

        /// <summary>Host only: sends the whole ledger to one joiner, in chunks.</summary>
        internal static void SendTo(Drova_Modding_API.Systems.Networking.INetPeer peer)
        {
            List<WorldObjectSnapshotMessage.Record> chunk = new();
            int total = 0;

            foreach (KeyValuePair<string, WorldObjectMessage> entry in _ledger)
            {
                chunk.Add(new WorldObjectSnapshotMessage.Record
                {
                    RootKey = entry.Value.RootKey,
                    Kind = entry.Value.Kind,
                    Values = entry.Value.Values,
                });

                total++;

                if (chunk.Count < WorldObjectSnapshotMessage.MaxRecords) continue;

                CoopSession.SendWorldObjectSnapshot(peer, [.. chunk]);
                chunk.Clear();
            }

            if (chunk.Count > 0) CoopSession.SendWorldObjectSnapshot(peer, [.. chunk]);

            CoopSession.SendWorldObjectSnapshotEnd(peer, (ushort)total);
        }

        private static void BoolInitPostfix(Saveable_GenericBoolState __instance, GameObject root)
        {
            Register(__instance, root, _boolByKey);
        }

        private static void FloatInitPostfix(Saveable_GenericFloatState __instance, GameObject root)
        {
            Register(__instance, root, _floatByKey);
        }

        private static void ActivationStartPostfix(Saveable_GameObject_ActivationState __instance)
        {
            Register(__instance, __instance.gameObject, _activationByKey);
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
        private static void Register<T>(T saveable, GameObject root, Dictionary<string, T> table) where T : Component
        {
            try
            {
                string? key = KeyOf(root != null ? root.transform : saveable.transform);
                if (key == null) return;

                table[key] = saveable;

                // A change can easily arrive before the chunk holding its object does, and the ledger
                // keeps it; this is the moment it becomes applicable.
                foreach (byte kind in Kinds)
                {
                    string ledgerKey = LedgerKey(key, kind);
                    if (_ledger.TryGetValue(ledgerKey, out WorldObjectMessage entry)) TryApply(entry);
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
            if (CoopSession.State is not (CoopSessionState.Hosting or CoopSessionState.Joined)) return;

            try
            {
                string? key = KeyOf(saveable.transform);
                if (key == null) return;

                table[key] = saveable;

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
                _sent++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[WorldObjects] reporting a world object failed: " + e);
            }
        }

        /// <summary>
        /// Writes an entry into this world's copy, if the copy is here and the shapes agree.
        /// </summary>
        private static void TryApply(in WorldObjectMessage message)
        {
            string key = LedgerKey(message.RootKey, message.Kind);

            try
            {
                using (RemoteApply<WorldObjectScope>.Enter())
                {
                    switch (message.Kind)
                    {
                        case WorldObjectMessage.KindBool:
                            if (!_boolByKey.TryGetValue(message.RootKey, out Saveable_GenericBoolState? bools) || bools == null) return;
                            if (!ApplyBool(bools, message.Values)) return;
                            break;

                        case WorldObjectMessage.KindFloat:
                            if (!_floatByKey.TryGetValue(message.RootKey, out Saveable_GenericFloatState? floats) || floats == null) return;
                            if (!ApplyFloat(floats, message.Values)) return;
                            break;

                        default:
                            if (!_activationByKey.TryGetValue(message.RootKey, out Saveable_GameObject_ActivationState? activation) || activation == null) return;
                            if (!ApplyActivation(activation, message.Values)) return;
                            break;
                    }
                }

                _pending.Remove(key);
                _applied++;
            }
            catch (Exception e)
            {
                _pending.Remove(key);
                MelonLogger.Error("[WorldObjects] applying a world object failed: " + e);
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

            return true;
        }

        private static bool ApplyFloat(Saveable_GenericFloatState saveable, float[] values)
        {
            Il2CppSystem.Collections.Generic.List<Il2CppDrova.Saveables.ISaveableFloat> list = saveable._saveBhvrs;
            if (list == null || list.Count != values.Length) return false;

            for (int index = 0; index < list.Count; index++)
            {
                list[index].OnLoadTrigger(values[index]);
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
            if (left.Length != right.Length) return false;

            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index]) return false;
            }

            return true;
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
        /// saves itself, or it saves under a root numbered per savegame.
        /// </summary>
        private static string? KeyOf(Transform transform)
        {
            ASaveRoot? root = transform.GetComponent<ASaveRoot>();
            if (root == null) root = transform.GetComponentInParent<ASaveRoot>();
            if (root == null) return null;

            SaveRoot_Dynamic? dynamic = root.TryCast<SaveRoot_Dynamic>();
            if (dynamic != null && dynamic.IsDynamic()) return null;

            string key = root.GetGameObjectInstanceKey();
            return ResourceWorkMessage.IsReplicableKey(key) ? key : null;
        }
    }
}
