using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using Il2CppDrova.Data;
using Il2CppDrova.HitSystem;
using Il2CppDrova.InteractionSystem;
using Il2CppDrova.Saveables;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Replicates door, lever, crate and gate state through the save-root list components, whose index order matches on every machine.
    /// State crosses through the game's load path, never the act. Resource spots belong to <see cref="ResourceWork"/>.
    /// </summary>
    internal sealed class WorldObjects : ICoopSystem
    {
        private const int ApplyBudgetPerFrame = 8;

        // A save root loads its saved state into these lists right after building them, so writing earlier gets overwritten.
        private const int ApplyDelayFrames = 5;

        private const int ApplyRetryFrames = 30;

        // An entry that never fits (mod sets disagreeing on a root's object count) is dropped after this many tries.
        private const int ApplyAttempts = 10;

        /// <summary>How many hooks a full install has.</summary>
        internal const int HookCount = 18;

        private static readonly byte[] Kinds =
        [
            WorldObjectMessage.KindBool,
            WorldObjectMessage.KindFloat,
            WorldObjectMessage.KindActivation,
            WorldObjectMessage.KindAlraune,
            WorldObjectMessage.KindElevator,
            WorldObjectMessage.KindSprite,
            WorldObjectMessage.KindDoor,
            WorldObjectMessage.KindLock,
        ];

        private static readonly Dictionary<string, Saveable_GenericBoolState> BoolByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Saveable_GenericFloatState> FloatByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Saveable_GameObject_ActivationState> ActivationByKey = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Saveable_Alraune> AlrauneByKey = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Saveable_Elevator> ElevatorByKey = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Saveable_HitBhvrSpriteChange> SpriteByKey = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Interact_Bhvr_Door> DoorByKey = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Saveable_Depot> DepotByKey = new(StringComparer.Ordinal);

        // A lock knows its depot, not the save component holding it, which can sit beside the lock (a trapdoor's "Saving").
        private static readonly Dictionary<IntPtr, string> DepotKeyByWrapper = new();

        private static readonly Dictionary<IntPtr, string> DoorKeyByDepot = new();

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

        string? ICoopSystem.HookSwitch => HookSwitches.WorldObjects;

        /// <summary>How many patches applied, of <see cref="HookCount"/>.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How many local changes this machine told the session about.</summary>
        internal static int Sent { get; private set; }

        /// <summary>How many of those were sprite-swap breakables. For tests.</summary>
        internal static int SpritesSent { get; private set; }

        /// <summary>How many remote changes this machine wrote into its own world.</summary>
        internal static int Applied { get; private set; }

        /// <summary>How many remote changes are still waiting for their object to stream in.</summary>
        internal static int Held => ApplyAt.Count;

        /// <summary>How many objects this machine's ledger has seen move.</summary>
        internal static int Known => Ledger.Count;

        /// <summary>How many objects are loaded here under a key a teammate could name.</summary>
        internal static int Addressable => BoolByKey.Count + FloatByKey.Count + ActivationByKey.Count + AlrauneByKey.Count
            + ElevatorByKey.Count + SpriteByKey.Count + DoorByKey.Count + DepotByKey.Count;

        /// <summary>How many entries were given up on because this world's copy never fit them.</summary>
        internal static int Abandoned { get; private set; }

        /// <summary>Whether the host's join snapshot has arrived in full.</summary>
        internal static bool SnapshotComplete { get; private set; }

        /// <summary>
        /// Applies the patches.
        /// Each component gets its list-building sweep (root and length known) and its state-changed listener.
        /// </summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GenericBoolState), nameof(Saveable_GenericBoolState.OnInitRoot),
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.BoolInitPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GenericBoolState), "OnStateChangedEvent",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.BoolChangedPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GenericFloatState), nameof(Saveable_GenericFloatState.OnInitRoot),
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.FloatInitPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GenericFloatState), "OnStateChangedEvent",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.FloatChangedPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GameObject_ActivationState), "Start",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.ActivationStartPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_GameObject_ActivationState), "OnStateChangedEventListener",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.ActivationChangedPostfix))) applied++;

            // The Alraune keeps pulled/caught as fields, and its creature is destroyed rather than killed, so it needs its own hooks.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_Alraune), "Start",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.AlrauneStartPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_Alraune), "InteractStartedListener",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.AlrauneChangedPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_Alraune), "ActorDestroyListener",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.AlrauneChangedPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_Elevator), nameof(Saveable_Elevator.Awake),
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.ElevatorAwakePostfix))) applied++;

            // The panel, not the controller: Operate is small enough to be inlined into it, and the far side calls Operate.
            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_ElevatorPanel), nameof(Interact_Bhvr_ElevatorPanel.BeginInteraction),
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.ElevatorPanelPostfix))) applied++;

            // Awake, not LoadCallback: loading an egg reenters the changed listener hooked below.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_HitBhvrSpriteChange), nameof(Saveable_HitBhvrSpriteChange.Awake),
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.SpriteAwakePostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_HitBhvrSpriteChange), nameof(Saveable_HitBhvrSpriteChange.OnSpriteChangedEventListener),
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.SpriteChangedPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_Door), "Start",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.DoorStartPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_Door), nameof(Interact_Bhvr_Door.BeginInteraction),
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.DoorInteractionPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_Depot), "Start",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.DepotStartPostfix))) applied++;

            // Every way a lock opens ends here: the key, a won lockpick, and the skipped minigame.
            if (Hooking.TryPostfix(harmony, typeof(Interact_Condition_Locked), "UnlockChest",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.LockUnlockedPostfix))) applied++;

            // A depot streamed in after its door or lock registered loads its saved state over what was applied.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_Depot), "LoadCallback",
                typeof(WorldObjectsPatches), nameof(WorldObjectsPatches.DepotLoadedPostfix))) applied++;

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
        private static void Reset()
        {
            // The address tables survive: they fill once per load, not per session.
            Ledger.Clear();
            ApplyAt.Clear();
            Attempts.Clear();
            ApplyNow.Clear();
            _frame = 0;
            Abandoned = 0;
            Sent = 0;
            SpritesSent = 0;
            Applied = 0;
            _snapshotReceived = 0;
            SnapshotComplete = false;
        }

        /// <summary>Writes waiting entries into objects that have since loaded.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
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
        private static void Park(in WorldObjectMessage message)
        {
            if (!message.IsReplicable()) return;

            string key = LedgerKey(message.RootKey, message.Kind);
            Ledger[key] = message;

            Requeue(key);
        }

        /// <summary>Counts a snapshot chunk from the host on the way in.</summary>
        private static void TakeSnapshot(in WorldObjectSnapshotMessage message)
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
        private static void CompleteSnapshot(int count)
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
            if (!Ledger.TryAdd(key, entry)) return false;

            Requeue(key);
            return true;
        }

        /// <summary>Host only: sends the whole ledger to one joiner, in chunks.</summary>
        internal static void SendTo(INetPeer peer)
        {
            foreach (WorldObjectSnapshotMessage.Record[] chunk in ExportLedger().Chunk(WorldObjectSnapshotMessage.MaxRecords))
            {
                SendWorldObjectSnapshot(peer, chunk);
            }

            int total = Ledger.Count;

            SendWorldObjectSnapshotEnd(peer, (ushort)Math.Min(total, ushort.MaxValue));
        }

        internal static void BoolInitPostfix(Saveable_GenericBoolState __instance)
        {
            Register(__instance, BoolByKey);
        }

        internal static void FloatInitPostfix(Saveable_GenericFloatState __instance)
        {
            Register(__instance, FloatByKey);
        }

        internal static void ActivationStartPostfix(Saveable_GameObject_ActivationState __instance)
        {
            Register(__instance, ActivationByKey);
        }

        internal static void AlrauneStartPostfix(Saveable_Alraune __instance)
        {
            Register(__instance, AlrauneByKey);

            // The creature sync shares its creature by this guid (EnemyRegistry.IsSharedIdentity).
            Il2CppDrova.Actor? creature = __instance._alrauneInstance;
            string guid = creature == null ? string.Empty : EnemyRegistry.GuidOf(creature);
            if (guid.Length > 0) EnemyRegistry.AuthoredCreatures.Add(guid);
        }

        internal static void DoorStartPostfix(Interact_Bhvr_Door __instance)
        {
            Register(__instance, DoorByKey);
            if (__instance != null && __instance._depot != null && KeyByComponent.TryGetValue(__instance.GetInstanceID(), out string? key))
            {
                DoorKeyByDepot[__instance._depot.Pointer] = key;
            }
        }

        // NPCs walking through and quest observers open doors too, each machine its own; only a player's hand crosses.
        internal static void DoorInteractionPostfix(Interact_Bhvr_Door __instance, InteractArgs? args)
        {
            if (args == null || !Players.RemotePlayers.IsLocalPlayer(args.Actor)) return;

            Broadcast(__instance, WorldObjectMessage.KindDoor, ReadDoor);
        }

        internal static void DepotStartPostfix(Saveable_Depot __instance)
        {
            Register(__instance, DepotByKey);
            if (__instance != null && __instance._depotWrapper != null && KeyByComponent.TryGetValue(__instance.GetInstanceID(), out string? key))
            {
                DepotKeyByWrapper[__instance._depotWrapper.Pointer] = key;
            }
        }

        internal static void LockUnlockedPostfix(Interact_Condition_Locked __instance)
        {
            Depot_Wrapper? depot = __instance == null ? null : __instance._depot;
            if (depot == null || !DepotKeyByWrapper.TryGetValue(depot.Pointer, out string? key)) return;
            if (!DepotByKey.TryGetValue(key, out Saveable_Depot? saveable) || saveable == null || saveable._depotWrapper?.Pointer != depot.Pointer) return;

            Broadcast(saveable, WorldObjectMessage.KindLock, saved => saved._depotWrapper == null || saved._depotWrapper.IsLocked() ? null : [0f]);

            // A lock that opens its door as it unlocks never goes through the door's own interaction.
            if (DoorKeyByDepot.TryGetValue(depot.Pointer, out string? doorKey) && DoorByKey.TryGetValue(doorKey, out Interact_Bhvr_Door? door)
                && door != null && door._depot?.Pointer == depot.Pointer)
            {
                Broadcast(door, WorldObjectMessage.KindDoor, ReadDoor);
            }
        }

        internal static void DepotLoadedPostfix(Saveable_Depot __instance)
        {
            Depot_Wrapper? depot = __instance == null ? null : __instance._depotWrapper;
            if (depot == null || Ledger.Count == 0) return;

            if (DoorKeyByDepot.TryGetValue(depot.Pointer, out string? doorKey)) RequeueIfKnown(doorKey, WorldObjectMessage.KindDoor);

            if (KeyByComponent.TryGetValue(__instance!.GetInstanceID(), out string? key)) RequeueIfKnown(key, WorldObjectMessage.KindLock);
        }

        private static void RequeueIfKnown(string rootKey, byte kind)
        {
            string ledgerKey = LedgerKey(rootKey, kind);
            if (Ledger.ContainsKey(ledgerKey)) Requeue(ledgerKey);
        }

        internal static void ElevatorAwakePostfix(Saveable_Elevator __instance)
        {
            Register(__instance, ElevatorByKey);
        }

        internal static void ElevatorPanelPostfix(Interact_Bhvr_ElevatorPanel __instance)
        {
            ElevatorController? elevator = __instance == null ? null : __instance._elevator;
            if (elevator == null) return;

            foreach (Saveable_Elevator saveable in ElevatorByKey.Values)
            {
                if (saveable == null || saveable._elevator == null || saveable._elevator.Pointer != elevator.Pointer) continue;

                Broadcast(saveable, WorldObjectMessage.KindElevator, ReadElevator);
                return;
            }
        }

        internal static void SpriteAwakePostfix(Saveable_HitBhvrSpriteChange __instance)
        {
            Register(__instance, SpriteByKey);
        }

        internal static void SpriteChangedPostfix(Saveable_HitBhvrSpriteChange __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            // Some eggs trigger on disable, so a chunk unloading would report them broken.
            if (!__instance.gameObject.activeInHierarchy) return;

            int before = Sent;
            Broadcast(__instance, WorldObjectMessage.KindSprite, ReadSprite);
            if (Sent != before) SpritesSent++;
        }

        // No loading check: both listeners fire only from play, never from the saveable restoring itself.
        internal static void AlrauneChangedPostfix(Saveable_Alraune __instance)
        {
            Broadcast(__instance, WorldObjectMessage.KindAlraune, ReadAlraune);
        }

        // Restoring a savegame fires these listeners, which would broadcast this client's saved doors over the host's.
        internal static void BoolChangedPostfix(Saveable_GenericBoolState __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            Broadcast(__instance, WorldObjectMessage.KindBool, ReadBool);
        }

        internal static void FloatChangedPostfix(Saveable_GenericFloatState __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            Broadcast(__instance, WorldObjectMessage.KindFloat, ReadFloat);
        }

        internal static void ActivationChangedPostfix(Saveable_GameObject_ActivationState __instance)
        {
            if (__instance.IsInLoadingProcess) return;

            Broadcast(__instance, WorldObjectMessage.KindActivation, ReadActivation);
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
                if (Ledger.Count == 0) return;

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

        private static void Broadcast<T>(T saveable, byte kind, Func<T, float[]?> read)
            where T : Component
        {
            if (RemoteApply<WorldObjectScope>.InProgress) return;
            if (!CoopSession.SharesWorld) return;

            try
            {
                if (!KeyByComponent.TryGetValue(saveable.GetInstanceID(), out string key)) return;

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
                ReportWorldObject(message);
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

                        case WorldObjectMessage.KindElevator:
                            if (!ElevatorByKey.TryGetValue(message.RootKey, out Saveable_Elevator? elevator) || elevator == null) return false;
                            if (!ApplyElevator(elevator, message.Values)) return false;
                            break;

                        case WorldObjectMessage.KindSprite:
                            if (!SpriteByKey.TryGetValue(message.RootKey, out Saveable_HitBhvrSpriteChange? sprites) || sprites == null) return false;
                            if (!ApplySprite(sprites, message.Values)) return false;
                            break;

                        case WorldObjectMessage.KindDoor:
                            if (!DoorByKey.TryGetValue(message.RootKey, out Interact_Bhvr_Door? door) || door == null) return false;
                            if (!ApplyDoor(door, message.Values)) return false;
                            break;

                        // Unlocked only: the lid stays as it is, since a teammate's loot window may hang on it.
                        case WorldObjectMessage.KindLock:
                            if (!DepotByKey.TryGetValue(message.RootKey, out Saveable_Depot? saved) || saved == null || saved._depotWrapper == null) return false;
                            if (saved._depotWrapper.IsLocked()) saved._depotWrapper.Unlock();
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

            Il2CppDrova.Actor? creature = saveable._alrauneInstance;
            if (creature != null) creature.gameObject.SetActive(pulled && !caught);

            return true;
        }

        /// <summary>Unlocks the loaded door nearest this player, as its key would, without telling the session. For tests.</summary>
        internal static string UnlockNearestDoor()
        {
            if (!PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor? player)) return "no player";

            Interact_Bhvr_Door? nearest = DoorByKey.Values.Where(door => door != null && door._depot != null)
                .OrderBy(door => Vector2.Distance(door.transform.position, player.transform.position)).FirstOrDefault();
            if (nearest == null) return "none";

            nearest._depot.Unlock();
            return "unlocked " + nearest.transform.position;
        }

        /// <summary>Every loaded door whose key or parent/name names the fragment, as "parent/name=open,locked", for tests.</summary>
        internal static string DescribeDoors(string fragment) => string.Join(";", DoorByKey
            .Where(entry => entry.Value != null && entry.Value._depot != null)
            .Select(entry => (Name: (entry.Value.transform.parent != null ? entry.Value.transform.parent.name + "/" : "") + entry.Value.name, entry.Key, entry.Value._depot))
            .Where(named => named.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase) || named.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(named => named.Key, StringComparer.Ordinal)
            .Select(named => $"{named.Name}={(named._depot.IsClosed() ? 0 : 1)},{(named._depot.IsLocked() ? 1 : 0)}"))
            is { Length: > 0 } text ? text : "none";

        /// <summary>The registered depots (chests, doors, trapdoors) within range of this player as "key=locked,ledger" (ledger: what the session last said), for tests.</summary>
        internal static string DescribeLocks(float range)
        {
            if (!PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor? player)) return "no player";

            string text = string.Join(";", DepotByKey
                .Where(entry => entry.Value != null && entry.Value._depotWrapper != null && Vector2.Distance(entry.Value.transform.position, player.transform.position) <= range)
                .Select(entry => $"{entry.Key}={(entry.Value._depotWrapper.IsLocked() ? 1 : 0)},{(Ledger.ContainsKey(LedgerKey(entry.Key, WorldObjectMessage.KindLock)) ? "unlock" : "-")}"));
            string ledger = string.Join(";", Ledger.Values.Where(entry => entry.Kind == WorldObjectMessage.KindLock).Select(entry => entry.RootKey));
            return (text.Length > 0 ? text : "none") + " | ledger " + (ledger.Length > 0 ? ledger : "none") + " | registered " + DepotByKey.Count;
        }

        /// <summary>Every loaded elevator as "state target moving enabled", for tests.</summary>
        internal static string DescribeElevators() => ElevatorByKey.Count == 0 ? "none" : string.Join(";", ElevatorByKey.Values
            .Where(saveable => saveable != null && saveable._elevator != null)
            .Select(saveable => $"{saveable._elevator.CurState} {saveable._elevator._targetState} moving={saveable._elevator.IsMoving} enabled={saveable._elevator.isActiveAndEnabled}"));

        private static float[]? ReadDoor(Interact_Bhvr_Door door)
        {
            Depot_Wrapper? depot = door._depot;
            return depot == null ? null : [depot.IsClosed() ? 0f : 1f, depot.IsLocked() ? 1f : 0f];
        }

        private static bool ApplyDoor(Interact_Bhvr_Door door, float[] values)
        {
            Depot_Wrapper? depot = door._depot;
            if (depot == null || values.Length < 2) return false;

            bool open = values[0] > 0.5f;
            bool locked = values[1] > 0.5f;

            // The door's own depot listener plays the swing.
            if (open && depot.IsClosed())
            {
                if (depot.IsLocked()) depot.UnlockAndOpen();
                else depot.Open();
            }
            else if (!open && !depot.IsClosed())
            {
                depot.Close();
            }

            if (locked && !depot.IsLocked()) depot.Lock();
            else if (!locked && depot.IsLocked()) depot.Unlock();

            return true;
        }

        private static float[]? ReadElevator(Saveable_Elevator saveable)
        {
            ElevatorController? elevator = saveable._elevator;
            if (elevator == null) return null;

            return [(float)(elevator.IsMoving ? elevator._targetState : elevator.CurState)];
        }

        // Operated rather than set, so a player standing on it rides along. It has two states, so operating reaches the other.
        private static bool ApplyElevator(Saveable_Elevator saveable, float[] values)
        {
            ElevatorController? elevator = saveable._elevator;
            if (elevator == null || values.Length != 1) return false;

            // Retried once the ride ends.
            if (elevator.IsMoving) return false;

            ElevatorController.State target = (ElevatorController.State)(int)values[0];
            if (elevator.CurState == target) return true;

            if (elevator.isActiveAndEnabled)
            {
                elevator.Operate();
                return true;
            }

            // Only a finished ride writes the save data.
            elevator.SetStateImmediate(target);
            if (saveable._saveData != null) saveable._saveData.State = target;
            return true;
        }

        private static float[]? ReadSprite(Saveable_HitBhvrSpriteChange saveable)
        {
            Il2CppSystem.Collections.Generic.List<HitReceiveBhvr_ChangeSprite> list = saveable._spriteChangeBhvrs;
            if (list == null) return null;

            float[] values = new float[list.Count];
            for (int index = 0; index < list.Count; index++)
            {
                values[index] = list[index] != null && list[index].Triggered ? 1f : 0f;
            }

            return values;
        }

        // Only ever on: an egg never closes again. The changed listener writes the save data.
        private static bool ApplySprite(Saveable_HitBhvrSpriteChange saveable, float[] values)
        {
            Il2CppSystem.Collections.Generic.List<HitReceiveBhvr_ChangeSprite> list = saveable._spriteChangeBhvrs;
            if (list == null || list.Count != values.Length) return false;

            for (int index = 0; index < list.Count; index++)
            {
                if (values[index] != 0f && list[index] != null && !list[index].Triggered) list[index].OnLoadTrigger(true);
            }

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

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<WorldObjectMessage>(MessageIds.WorldObject, OnWorldObject);
            CoopSession.Register<WorldObjectSnapshotMessage>(MessageIds.WorldObjectSnapshot, OnWorldObjectSnapshot);
            CoopSession.Register<WorldObjectSnapshotEndMessage>(MessageIds.WorldObjectSnapshotEnd, OnWorldObjectSnapshotEnd);
        }

        /// <summary>Reports a world object change here, like a door, lever or crate. See <see cref="WorldObjects"/>.</summary>
        private static void ReportWorldObject(in WorldObjectMessage message)
            => CoopSession.SendIfLive(message);

        /// <summary>Host to one joiner: a chunk of the world-object ledger.</summary>
        private static void SendWorldObjectSnapshot(INetPeer peer, WorldObjectSnapshotMessage.Record[] records)
        {
            if (!CoopSession.HostsWorld) return;

            NetworkAccess.Send(peer, new WorldObjectSnapshotMessage { Records = records });
        }

        /// <summary>Host to one joiner: the world-object ledger is complete.</summary>
        private static void SendWorldObjectSnapshotEnd(INetPeer peer, ushort count)
        {
            if (!CoopSession.HostsWorld) return;

            NetworkAccess.Send(peer, new WorldObjectSnapshotEndMessage { Count = count });
        }

        private static void OnWorldObject(INetPeer peer, WorldObjectMessage message)
        {
            if (!message.IsReplicable()) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!CoopSession.HostsWorld) return;

                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                message.Player = sender.Value;

                Park(message);
                CoopSession.SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!CoopSession.IsFromHost()) return;

            Park(message);
        }

        private static void OnWorldObjectSnapshot(INetPeer peer, WorldObjectSnapshotMessage message)
        {
            if (!CoopSession.IsFromHost()) return;

            TakeSnapshot(message);
        }

        private static void OnWorldObjectSnapshotEnd(INetPeer peer, WorldObjectSnapshotEndMessage message)
        {
            if (!CoopSession.IsFromHost()) return;

            CompleteSnapshot(message.Count);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
