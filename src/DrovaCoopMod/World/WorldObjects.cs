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
    /// Replicates door, lever, crate and gate state through the save-root list components, whose index order matches on every machine.
    /// State crosses through the game's load path, never the act. Resource spots belong to <see cref="ResourceWork"/>.
    /// </summary>
    internal static class WorldObjects
    {
        private const int ApplyBudgetPerFrame = 8;

        // A save root loads its saved state into these lists right after building them, so writing earlier gets overwritten.
        private const int ApplyDelayFrames = 5;

        private const int ApplyRetryFrames = 30;

        // An entry that never fits (mod sets disagreeing on a root's object count) is dropped after this many tries.
        private const int ApplyAttempts = 10;

        private static readonly Dictionary<string, Saveable_GenericBoolState> BoolByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Saveable_GenericFloatState> FloatByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Saveable_GameObject_ActivationState> ActivationByKey = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Saveable_Alraune> AlrauneByKey = new(StringComparer.Ordinal);

        // Everything the session has seen move. The host's copy is what a joiner gets.
        private static readonly Dictionary<string, WorldObjectMessage> Ledger = new(StringComparer.Ordinal);

        // Pending entries and the _frame at which each is next tried.
        private static readonly Dictionary<string, int> ApplyAt = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, int> Attempts = new(StringComparer.Ordinal);

        private static readonly List<string> ApplyNow = new();

        // The key fixed at registration, so a component reports under the same name it was filed under.
        private static readonly Dictionary<int, string> KeyByComponent = new();

        private static int _frame;

        private static int _snapshotReceived;

        /// <summary>How many patches applied, of <see cref="HookCount"/>.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How many hooks a full install has.</summary>
        internal const int HookCount = 9;

        /// <summary>How many local changes this machine told the session about.</summary>
        internal static int Sent { get; private set; }

        /// <summary>How many remote changes this machine wrote into its own world.</summary>
        internal static int Applied { get; private set; }

        /// <summary>How many remote changes are still waiting for their object to stream in.</summary>
        internal static int Held => ApplyAt.Count;

        /// <summary>How many objects this machine's ledger has seen move.</summary>
        internal static int Known => Ledger.Count;

        /// <summary>How many objects are loaded here under a key a teammate could name.</summary>
        internal static int Addressable => BoolByKey.Count + FloatByKey.Count + ActivationByKey.Count + AlrauneByKey.Count;

        /// <summary>How many entries were given up on because this world's copy never fit them.</summary>
        internal static int Abandoned { get; private set; }

        /// <summary>Whether the host's join snapshot has arrived in full.</summary>
        internal static bool SnapshotComplete { get; private set; }

        private static bool IsHost => CoopSession.State == CoopSessionState.Hosting;

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// Each component gets its list-building sweep (root and length known) and its state-changed listener.
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

            // The Alraune keeps pulled/caught as fields, and its creature is destroyed rather than killed, so it needs its own hooks.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_Alraune), "Start",
                typeof(WorldObjects), nameof(AlrauneStartPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_Alraune), "InteractStartedListener",
                typeof(WorldObjects), nameof(AlrauneChangedPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_Alraune), "ActorDestroyListener",
                typeof(WorldObjects), nameof(AlrauneChangedPostfix))) applied++;

            HooksInstalled = applied;

            if (applied == HookCount)
            {
                MelonLogger.Msg($"[WorldObjects] {HookCount} of {HookCount} hooks installed; world objects can cross.");
            }
            else
            {
                MelonLogger.Warning($"[WorldObjects] {applied} of {HookCount} hooks installed; world objects will only half cross. " +
                    "coop_status says which counts are moving.");
            }
        }

        /// <summary>Forgets everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            // The address tables survive: they fill once per load, not per session.
            Ledger.Clear();
            ApplyAt.Clear();
            Attempts.Clear();
            ApplyNow.Clear();
            KeyByComponent.Clear();
            _frame = 0;
            Abandoned = 0;
            Sent = 0;
            Applied = 0;
            _snapshotReceived = 0;
            SnapshotComplete = false;
        }

        /// <summary>Writes waiting entries into objects that have since loaded. Called once a frame.</summary>
        internal static void Update()
        {
            _frame++;

            if (ApplyAt.Count == 0) return;

            ApplyNow.Clear();
            foreach ((string key, int due) in ApplyAt)
            {
                if (_frame < due) continue;

                ApplyNow.Add(key);
                if (ApplyNow.Count >= ApplyBudgetPerFrame) break;
            }

            foreach (string key in ApplyNow)
            {
                if (!Ledger.TryGetValue(key, out WorldObjectMessage entry))
                {
                    Forget(key);
                    continue;
                }

                if (TryApply(entry)) continue;

                int attempts = Attempts.GetValueOrDefault(key) + 1;
                Attempts[key] = attempts;

                if (attempts >= ApplyAttempts)
                {
                    Forget(key);
                    Abandoned++;
                    continue;
                }

                ApplyAt[key] = _frame + ApplyRetryFrames;
            }

            ApplyNow.Clear();
        }

        private static void Forget(string key)
        {
            ApplyAt.Remove(key);
            Attempts.Remove(key);
        }

        private static void Requeue(string key)
        {
            Attempts.Remove(key);
            ApplyAt[key] = _frame + ApplyDelayFrames;
        }

        /// <summary>Records an arrived entry and queues it, since its save root may still be loading.</summary>
        internal static void Park(in WorldObjectMessage message)
        {
            if (!message.IsReplicable()) return;

            string key = LedgerKey(message.RootKey, message.Kind);
            Ledger[key] = message;

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
            foreach (KeyValuePair<string, WorldObjectMessage> entry in Ledger)
            {
                yield return new WorldObjectSnapshotMessage.Record
                {
                    RootKey = entry.Value.RootKey,
                    Kind = entry.Value.Kind,
                    Values = entry.Value.Values,
                };
            }
        }

        /// <summary>Puts an object changed in an earlier session back on the ledger.</summary>
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
            if (Ledger.ContainsKey(key)) return false;

            Ledger[key] = entry;
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

            int total = Ledger.Count;

            CoopSession.SendWorldObjectSnapshotEnd(peer, (ushort)Math.Min(total, ushort.MaxValue));
        }

        private static void BoolInitPostfix(Saveable_GenericBoolState __instance)
        {
            Register(__instance, BoolByKey);
        }

        private static void FloatInitPostfix(Saveable_GenericFloatState __instance)
        {
            Register(__instance, FloatByKey);
        }

        private static void ActivationStartPostfix(Saveable_GameObject_ActivationState __instance)
        {
            Register(__instance, ActivationByKey);
        }

        private static void AlrauneStartPostfix(Saveable_Alraune __instance)
        {
            Register(__instance, AlrauneByKey);

            // The creature sync shares its creature by this guid (EnemyRegistry.IsSharedIdentity).
            if (__instance?._alrauneInstance?.GetGuidComponent()?.GuidString is { Length: > 0 } guid) EnemyRegistry.AuthoredCreatures.Add(guid);
        }

        // No loading check: both listeners fire only from play, never from the saveable restoring itself.
        private static void AlrauneChangedPostfix(Saveable_Alraune __instance)
        {
            Broadcast(__instance, WorldObjectMessage.KindAlraune, AlrauneByKey, ReadAlraune);
        }

        // Restoring a savegame fires these listeners, which would broadcast this client's saved doors over the host's.
        private static void BoolChangedPostfix(Saveable_GenericBoolState __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            Broadcast(__instance, WorldObjectMessage.KindBool, BoolByKey, ReadBool);
        }

        private static void FloatChangedPostfix(Saveable_GenericFloatState __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            Broadcast(__instance, WorldObjectMessage.KindFloat, FloatByKey, ReadFloat);
        }

        private static void ActivationChangedPostfix(Saveable_GameObject_ActivationState __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            Broadcast(__instance, WorldObjectMessage.KindActivation, ActivationByKey, ReadActivation);
        }

        /// <summary>Files a component under its save root's key and queues anything waiting for it.</summary>
        private static void Register<T>(T saveable, Dictionary<string, T> table) where T : Component
        {
            try
            {
                // From the component itself, not the root handed in, since the two can differ.
                string? key = KeyOf(saveable.transform);
                if (key == null) return;

                table[key] = saveable;
                KeyByComponent[saveable.GetInstanceID()] = key;

                // Queued, not written: the root loads its saved state into these lists right after this sweep.
                foreach (byte kind in Kinds)
                {
                    string ledgerKey = LedgerKey(key, kind);
                    if (!Ledger.ContainsKey(ledgerKey)) continue;

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
            WorldObjectMessage.KindAlraune,
        ];

        private static void Broadcast<T>(T saveable, byte kind, Dictionary<string, T> table, Func<T, float[]?> read)
            where T : Component
        {
            if (RemoteApply<WorldObjectScope>.InProgress) return;
            if (!CoopSession.IsLive) return;

            try
            {
                if (!KeyByComponent.TryGetValue(saveable.GetInstanceID(), out string? key)) return;

                float[]? values = read(saveable);

                // Too long for the wire is skipped, not cut, since a cut list puts indices out of step.
                if (values == null || values.Length > WorldObjectMessage.MaxValues) return;

                WorldObjectMessage message = new()
                {
                    RootKey = key,
                    Kind = kind,
                    Values = values,
                };

                string ledgerKey = LedgerKey(key, kind);

                // The listeners also fire on writes of the same value.
                if (Ledger.TryGetValue(ledgerKey, out WorldObjectMessage known) && Same(known.Values, values)) return;

                Ledger[ledgerKey] = message;
                CoopSession.ReportWorldObject(message);
                Sent++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[WorldObjects] reporting a world object failed: " + e);
            }
        }

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
                            if (!BoolByKey.TryGetValue(message.RootKey, out Saveable_GenericBoolState? bools) || bools == null) return false;
                            if (!ApplyBool(bools, message.Values)) return false;
                            break;

                        case WorldObjectMessage.KindFloat:
                            if (!FloatByKey.TryGetValue(message.RootKey, out Saveable_GenericFloatState? floats) || floats == null) return false;
                            if (!ApplyFloat(floats, message.Values)) return false;
                            break;

                        case WorldObjectMessage.KindAlraune:
                            if (!AlrauneByKey.TryGetValue(message.RootKey, out Saveable_Alraune? alraune) || alraune == null) return false;
                            if (!ApplyAlraune(alraune, message.Values)) return false;
                            break;

                        default:
                            if (!ActivationByKey.TryGetValue(message.RootKey, out Saveable_GameObject_ActivationState? activation) || activation == null) return false;
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
            Il2CppSystem.Collections.Generic.List<ISaveableBool> list = saveable._spriteChangeBhvrs;
            if (list == null || list.Count != values.Length) return false;

            for (int index = 0; index < list.Count; index++)
            {
                if (IsRubblePile(list[index])) continue;

                list[index].OnLoadTrigger(values[index] != 0f);
            }

            WriteBoolSaveData(saveable, values);
            return true;
        }

        /// <summary>
        /// A rock face's rubble flag, skipped because loading it true spawns a second pile. <see cref="HaulShare"/> owns piles.
        /// </summary>
        private static bool IsRubblePile(ISaveableBool entry)
        {
            return entry.TryCast<Il2CppDrova.InteractionSystem.Interact_Bhvr_Spot_Rubble>() != null;
        }

        /// <summary>
        /// Writes the applied state into the save data, which the component fills only from the change event.
        /// </summary>
        private static void WriteBoolSaveData(Saveable_GenericBoolState saveable, float[] values)
        {
            SaveData_BoolList? data = saveable._saveData;
            if (data == null || data.Value == null || data.Value.Length != values.Length) return;

            Il2CppSystem.Collections.Generic.List<ISaveableBool> list = saveable._spriteChangeBhvrs;

            for (int index = 0; index < values.Length; index++)
            {
                if (list != null && index < list.Count && IsRubblePile(list[index])) continue;

                data.Value[index] = values[index] != 0f;
            }
        }

        private static bool ApplyFloat(Saveable_GenericFloatState saveable, float[] values)
        {
            Il2CppSystem.Collections.Generic.List<ISaveableFloat> list = saveable._saveBhvrs;
            if (list == null || list.Count != values.Length) return false;

            for (int index = 0; index < list.Count; index++)
            {
                list[index].OnLoadTrigger(values[index]);
            }

            // As with the bool list, the saved value is only written from the change event.
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
            Il2CppSystem.Collections.Generic.List<ISaveableBool> list = saveable._spriteChangeBhvrs;
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
            Il2CppSystem.Collections.Generic.List<ISaveableFloat> list = saveable._saveBhvrs;
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

        private static float[]? ReadAlraune(Saveable_Alraune saveable)
        {
            Saveable_Alraune.Data? data = saveable._saveData;
            if (data == null) return null;

            return [data.Interacted ? 1f : 0f, data.Picked ? 1f : 0f];
        }

        /// <summary>
        /// Mirrors the saveable's own load. A caught creature is deactivated, not destroyed, so its destroy listeners stay with the catcher.
        /// </summary>
        private static bool ApplyAlraune(Saveable_Alraune saveable, float[] values)
        {
            Saveable_Alraune.Data? data = saveable._saveData;
            if (data == null || values.Length != 2) return false;

            bool pulled = values[0] != 0f || data.Interacted;
            bool caught = values[1] != 0f || data.Picked;
            data.Interacted = pulled;
            data.Picked = caught;

            if (pulled)
            {
                if (saveable._interaction != null) saveable._interaction.SetActive(false);

                Il2CppSystem.Collections.Generic.List<GameObject> others = saveable._additionalDeactivates;
                for (int index = 0; others != null && index < others.Count; index++)
                {
                    if (others[index] != null) others[index].SetActive(false);
                }
            }

            Il2CppDrova.Actor creature = saveable._alrauneInstance;
            if (creature != null) creature.gameObject.SetActive(pulled && !caught);

            return true;
        }

        private static bool Same(float[]? left, float[]? right)
        {
            if (left == null || right == null) return false;

            return left.AsSpan().SequenceEqual(right);
        }

        // One root can carry several of the lists, so the ledger is keyed by root and kind.
        private static string LedgerKey(string rootKey, byte kind)
        {
            return rootKey + "|" + kind;
        }

        /// <summary>
        /// A save root beside the object, as a loot chest keeps its <c>Saving</c> next to its <c>Interaction</c>.
        /// One level only, so an object under a scene holder does not borrow a neighbor's key.
        /// </summary>
        private static ASaveRoot? SiblingRoot(Transform transform)
        {
            Transform? parent = transform.parent;
            if (parent == null) return null;

            for (int index = 0; index < parent.childCount; index++)
            {
                ASaveRoot? sibling = parent.GetChild(index).GetComponent<ASaveRoot>();
                if (sibling != null) return sibling;
            }

            return null;
        }

        /// <summary>A scene object's save root key, or null when it has none or its root is numbered per savegame.</summary>
        internal static string? KeyOf(Component component)
        {
            ASaveRoot? root = component.GetComponent<ASaveRoot>();
            if (root == null) root = component.GetComponentInParent<ASaveRoot>();
            if (root == null) root = SiblingRoot(component.transform);
            if (root == null) return null;

            SaveRoot_Dynamic? dynamic = root.TryCast<SaveRoot_Dynamic>();
            if (dynamic != null && dynamic.IsDynamic()) return null;

            string key = root.GetGameObjectInstanceKey();
            return ResourceWorkMessage.IsReplicableKey(key) ? key : null;
        }
    }
}
