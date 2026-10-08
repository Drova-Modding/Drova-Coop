using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Diagnostics;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Loot.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppDrova;
using Il2CppDrova.DialogueNew;
using Il2CppDrova.InteractionSystem;
using Il2CppDrova.InventorySystem;
using Il2CppDrova.Items;
using Il2CppDrova.Saveables;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Loot
{
    /// <summary>
    /// One world's worth of loot under the shared loot rule: pickups are removed everywhere, containers set to their remainder.
    /// Each game rolls its own chests, so contents are offered on stream-in and the host keeps the first offer.
    /// Only <c>Inventory_LootBhvr</c> containers are shared, never one holding a player's own things.
    /// </summary>
    internal sealed class LootShare : ICoopSystem
    {
        private const int ApplyBudgetPerFrame = 8;
        private const int MaxPendingLootSnapshots = 4096;

        /// <summary>
        /// Frames between a container registering and its offer, since the save root restoring its contents
        /// may <c>Start</c> after it in the same frame.
        /// </summary>
        private const int ProposeDelayFrames = 5;

        /// <summary>Frames before a container that was open when its turn came is looked at again.</summary>
        private const int ProposeRetryFrames = 30;

        /// <summary>The most offers one frame sends.</summary>
        private const int ProposeBudgetPerFrame = 4;

        /// <summary>Frames between two looks at a body whose creature was not named yet.</summary>
        private const int BodyRetryFrames = 15;

        /// <summary>How many looks a body gets before it is left to this world alone.</summary>
        private const int BodyAttempts = 20;

        /// <summary>
        /// Chests a story stows the player's entire inventory in, which carry an <c>Inventory_LootBhvr</c> anyway:
        /// the Lothar save object, the bandit mine and Eugen's Ripper hunt.
        /// </summary>
        private static readonly HashSet<string> NeverShared = new(StringComparer.OrdinalIgnoreCase)
        {
            "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
            "3d8311f8-6bed-4232-abb8-9c11df2f17ed",
            "a1a18a53-59a5-4f37-b90d-19386dd45701",
        };

        private static readonly Dictionary<string, Interact_Bhvr_LootInventory> ContainerByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<int, string> KeyByContainer = new();

        private static readonly Dictionary<string, Saveable_PickUp_Once> PickupByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<int, string> KeyByPickup = new();

        /// <summary>Everything shared loot has seen go this session, by key. The host's copy is what a joiner gets.</summary>
        private static readonly Dictionary<string, LootTakenMessage> Ledger = new(StringComparer.Ordinal);

        /// <summary>Keys whose latest state has not reached the local copy yet.</summary>
        private static readonly HashSet<string> Pending = new(StringComparer.Ordinal);

        /// <summary>Inventories a player's own things were moved into, or that a player deposited into. Never shared again this session.</summary>
        private static readonly HashSet<string> PrivateKeys = new(StringComparer.Ordinal);

        private static readonly List<string> ApplyNow = new();

        /// <summary>Containers whose contents are still to be offered, by key, with the frame to do it in.</summary>
        private static readonly Dictionary<string, int> ProposeAt = new(StringComparer.Ordinal);

        private static readonly List<string> ProposeNow = new();

        /// <summary>Bodies whose creature was not part of the session yet, by instance id.</summary>
        private static readonly Dictionary<int, Interact_Bhvr_LootInventory> Bodies = new();

        /// <summary>When each of those is worth another look, as a reading of <see cref="_frame"/>.</summary>
        private static readonly Dictionary<int, int> BodyRetryAt = new();

        private static readonly List<int> BodyRetryNow = new();

        /// <summary>How many times each waiting body has been looked at.</summary>
        private static readonly Dictionary<int, int> AttemptsByBody = new();

        private static readonly Queue<LootSnapshotMessage> IncomingLootSnapshot = new();
        private static readonly Dictionary<(int Peer, string Key), LootStack[]> LootParts = new();

        private static int _frame;

        private static bool _snapshotDone;

        private static int _snapshotReceived;

        private static bool _snapshotComplete;

        string? ICoopSystem.HookSwitch => HookSwitches.Loot;

        /// <summary>How many of the six patches applied.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How many takes this machine told the session about.</summary>
        internal static int Sent { get; private set; }

        /// <summary>How many remote takes this machine applied to its own world.</summary>
        internal static int Applied { get; private set; }

        /// <summary>How many remote takes are still waiting for their object to load, or their window to close.</summary>
        internal static int Held => Pending.Count;

        /// <summary>How many pieces of loot the session has seen go, on this machine's ledger.</summary>
        internal static int Known => Ledger.Count;

        /// <summary>How many containers and pickups are loaded here under a key a teammate could name.</summary>
        internal static int Addressable => ContainerByKey.Count + PickupByKey.Count;

        /// <summary>How many containers this machine offered the contents of.</summary>
        internal static int Proposed { get; private set; }

        /// <summary>Host only: how many offers lost to an entry that already stood, or to the host's own copy.</summary>
        internal static int ProposalsRefused { get; private set; }

        /// <summary>How many corpses and knocked-out bodies this machine named by their creature.</summary>
        internal static int BodiesNamed { get; private set; }

        /// <summary>How many bodies are still waiting for their creature to be part of the session.</summary>
        internal static int BodiesWaiting => Bodies.Count;

        private static bool IsHost => CoopSession.State == CoopSessionState.Hosting;

        private static bool Shared => CoopSession.LootMode == CoopProtocol.LootModeShared;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            // On load and on spawn, so a take that arrived first can land.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_PickUp_Once), nameof(Saveable_PickUp_Once.Start),
                typeof(LootSharePatches), nameof(LootSharePatches.PickupStartPostfix))) applied++;

            // The same funnel HaulShare listens on. Only one of them recognizes any given root.
            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_LootAll), nameof(Interact_Bhvr_LootAll.LootAll),
                typeof(LootSharePatches), nameof(LootSharePatches.LootAllPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_LootInventory), nameof(Interact_Bhvr_LootInventory.Start),
                typeof(LootSharePatches), nameof(LootSharePatches.ContainerStartPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_LootInventory), nameof(Interact_Bhvr_LootInventory.InventoryChangedEventListener),
                typeof(LootSharePatches), nameof(LootSharePatches.InventoryChangedPostfix))) applied++;

            // The take that empties a container closes its window inside the listener, before the postfix can look.
            Hooking.TryPrefix(harmony, typeof(Interact_Bhvr_LootInventory), nameof(Interact_Bhvr_LootInventory.InventoryChangedEventListener),
                typeof(LootSharePatches), nameof(LootSharePatches.InventoryChangedPrefix));

            // Right before the window is laid out, the one place a held remainder can go in without stale slots showing.
            if (Hooking.TryPrefix(harmony, typeof(Interact_Bhvr_LootInventory), nameof(Interact_Bhvr_LootInventory.CreateLootInventory),
                typeof(LootSharePatches), nameof(LootSharePatches.CreateLootInventoryPrefix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(DS_TransferInventoryNode), nameof(DS_TransferInventoryNode.Transferring),
                typeof(LootSharePatches), nameof(LootSharePatches.TransferPostfix))) applied++;

            HooksInstalled = applied;

            if (applied == 6)
            {
                MelonLogger.Msg("[Loot] 6 of 6 hooks installed; loot can be shared when the host's rule says so.");
            }
            else
            {
                MelonLogger.Warning($"[Loot] {applied} of 6 hooks installed; shared loot will only half work. " +
                    "coop_status says which counts are moving.");
            }
        }

        /// <summary>Forgets everything session-scoped. On leaving a session and on a savegame load inside one.</summary>
        private static void Reset()
        {
            Ledger.Clear();
            Pending.Clear();
            PrivateKeys.Clear();
            ApplyNow.Clear();
            _snapshotReceived = 0;
            _snapshotComplete = false;
            _snapshotDone = false;
            Sent = 0;
            Applied = 0;
            Proposed = 0;
            ProposalsRefused = 0;
            BodiesNamed = 0;

            Bodies.Clear();
            BodyRetryAt.Clear();
            AttemptsByBody.Clear();
            BodyRetryNow.Clear();

            // Creature keys name session handles, which the next session hands out again from zero.
            foreach (string key in ContainerByKey.Keys.Where(LootTakenMessage.IsCreatureKey).ToList())
            {
                if (ContainerByKey.TryGetValue(key, out Interact_Bhvr_LootInventory? body) && body != null)
                {
                    KeyByContainer.Remove(body.GetInstanceID());
                }

                ContainerByKey.Remove(key);
                ProposeAt.Remove(key);
            }

            // Everything still loaded is offered again to the next session.
            foreach (string key in ContainerByKey.Keys)
            {
                ProposeAt[key] = _frame + ProposeDelayFrames;
            }
        }

        /// <summary>Drains the join snapshot and applies whatever became applicable.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            _frame++;
            RetryBodies();
            int budget = ApplyBudgetPerFrame;

            while (budget-- > 0 && TryTakeLootSnapshot(out LootSnapshotMessage chunk))
            {
                if (chunk.Records == null) continue;

                foreach (LootSnapshotMessage.Record record in chunk.Records)
                {
                    _snapshotReceived++;

                    Park(new LootTakenMessage
                    {
                        RootKey = record.RootKey,
                        Kind = record.Kind,
                        Player = PlayerId.Host.Value,
                        Remaining = record.Remaining ?? [],
                    });
                }
            }

            if (_snapshotComplete)
            {
                _snapshotComplete = false;
                MelonLogger.Msg($"[Loot] {_snapshotReceived} piece(s) of loot the session had already seen go " +
                    $"arrived; {Pending.Count} wait for their part of the world.");
            }

            if (ApplyNow.Count > 0)
            {
                foreach (string key in ApplyNow)
                {
                    TryApply(key);
                }

                ApplyNow.Clear();
            }

            ProposeDue();
        }

        /// <summary>Offers every container whose delay has run out. A client waits for the host's ledger first.</summary>
        private static void ProposeDue()
        {
            if (ProposeAt.Count == 0) return;
            if (!CoopSession.IsLive || !Shared) return;
            if (!IsHost && !_snapshotDone) return;

            ProposeNow.Clear();

            foreach (KeyValuePair<string, int> due in ProposeAt)
            {
                if (due.Value > _frame) continue;

                ProposeNow.Add(due.Key);
                if (ProposeNow.Count >= ProposeBudgetPerFrame) break;
            }

            foreach (string key in ProposeNow)
            {
                ProposeAt.Remove(key);

                try
                {
                    Propose(key);
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[Loot] offering a container's contents failed: " + e);
                }
            }
        }

        /// <summary>
        /// Offers one container's contents unless the session already knows it. On the host, a client's offer is replaced.
        /// </summary>
        private static void Propose(string key)
        {
            if (PrivateKeys.Contains(key)) return;
            if (!ContainerByKey.TryGetValue(key, out Interact_Bhvr_LootInventory? container) || container == null) return;
            if (Ledger.TryGetValue(key, out LootTakenMessage existing) && !HostOutranks(existing)) return;

            // Open here, a take decides it. Looked at again later, in case none comes.
            if (container._inventoryOpened)
            {
                ProposeAt[key] = _frame + ProposeRetryFrames;
                return;
            }

            // The accessor, since a body's _ownerInventory field stays null.
            if (container.OwnerInventory == null) return;

            LootTakenMessage message = new()
            {
                RootKey = key,
                Kind = LootTakenMessage.KindProposal,
                Player = CoopSession.LocalPlayerId.Value,
                Remaining = Snapshot(container.OwnerInventory),
            };

            Ledger[key] = message;
            Pending.Remove(key);
            Proposed++;

            ReportLootTaken(message);
        }

        /// <summary>Whether this machine's copy outranks the entry: only the host's, and only over a client's offer.</summary>
        private static bool HostOutranks(in LootTakenMessage entry)
        {
            return IsHost && entry.Kind == LootTakenMessage.KindProposal && entry.Player != PlayerId.Host.Value;
        }

        /// <summary>Host only: whether a client's offer is adopted and should go to the others.</summary>
        private static bool TryAcceptProposal(in LootTakenMessage message, out LootTakenMessage? answer)
        {
            answer = null;
            if (!message.IsPlausible()) return false;

            if (Ledger.TryGetValue(message.RootKey, out LootTakenMessage existing))
            {
                ProposalsRefused++;
                answer = existing;
                return false;
            }

            if (ContainerByKey.ContainsKey(message.RootKey) || PrivateKeys.Contains(message.RootKey))
            {
                ProposalsRefused++;
                return false;
            }

            Park(message);
            return true;
        }

        /// <summary>Marks the snapshot complete.</summary>
        private static void CompleteSnapshot(ushort count)
        {
            _snapshotComplete = true;
            _snapshotDone = true;

            if (count != _snapshotReceived)
            {
                MelonLogger.Warning($"[Loot] the host announced {count} piece(s) of loot and {_snapshotReceived} arrived.");
            }
        }

        /// <summary>The pickups the party has taken, for the savegame. Containers are settled by their offers.</summary>
        internal static IEnumerable<string> ExportTakenPickups()
        {
            foreach (KeyValuePair<string, LootTakenMessage> entry in Ledger)
            {
                if (entry.Value.Kind != LootTakenMessage.KindPickup) continue;

                if (LootTakenMessage.IsCreatureKey(entry.Key)) continue;

                yield return entry.Key;
            }
        }

        /// <summary>Puts a pickup taken in an earlier session back on the ledger, so a joiner is told about it.</summary>
        internal static bool ImportTakenPickup(string rootKey)
        {
            LootTakenMessage entry = new()
            {
                RootKey = rootKey,
                Kind = LootTakenMessage.KindPickup,
                Player = CoopSession.LocalPlayerId.Value,
                Remaining = [],
            };

            if (!entry.IsPlausible()) return false;

            // Not pending, since the host's own copy is already gone and would never register.
            return Ledger.TryAdd(rootKey, entry);
        }

        /// <summary>Sends a joining player everything the ledger holds. Host only.</summary>
        internal static void SendTo(INetPeer peer)
        {
            try
            {
                LootSnapshotMessage.Record[] records = Ledger.Values
                    .Select(entry => new LootSnapshotMessage.Record
                    {
                        RootKey = entry.RootKey,
                        Kind = entry.Kind,
                        Remaining = entry.Remaining ?? [],
                    })
                    .ToArray();

                foreach (LootSnapshotMessage.Record[] chunk in records.Chunk(LootSnapshotMessage.MaxRecords))
                {
                    NetworkAccess.Send(peer, new LootSnapshotMessage { Records = chunk });
                }

                int sent = records.Length;

                NetworkAccess.Send(peer, new LootSnapshotEndMessage { Count = (ushort)Math.Min(sent, ushort.MaxValue) });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] sending the loot ledger to a joining player failed: " + e);
            }
        }

        /// <summary>Records a take from the session, applied once the object is here and free.</summary>
        private static void Park(in LootTakenMessage message)
        {
            if (!message.IsPlausible()) return;

            Ledger[message.RootKey] = message;
            Pending.Add(message.RootKey);
            ApplyNow.Add(message.RootKey);
        }

        internal static void PickupStartPostfix(Saveable_PickUp_Once __instance)
        {
            try
            {
                if (__instance == null) return;

                // A spilled pickup's guid is random per game. CacheShare shares caches instead.
                if (CacheShare.WasSpilled(__instance)) return;

                string? key = WorldObjects.KeyOf(__instance);
                if (key == null) return;

                KeyByPickup[__instance.GetInstanceID()] = key;
                PickupByKey[key] = __instance;

                if (Pending.Contains(key)) ApplyNow.Add(key);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] registering a pickup failed: " + e);
            }
        }

        internal static void LootAllPostfix(Interact_Bhvr_LootAll __instance, Actor actor)
        {
            try
            {
                if (!CoopSession.IsLive || !Shared) return;
                if (RemoteApply<LootScope>.InProgress) return;
                if (__instance == null || !RemotePlayers.IsLocalPlayer(actor)) return;

                Saveable_PickUp_Once? pickup = __instance.GetComponent<Saveable_PickUp_Once>();
                if (pickup == null) pickup = __instance.GetComponentInParent<Saveable_PickUp_Once>();
                if (pickup == null) return;

                if (!KeyByPickup.TryGetValue(pickup.GetInstanceID(), out string key)) return;

                LootTakenMessage message = new()
                {
                    RootKey = key,
                    Kind = LootTakenMessage.KindPickup,
                    Player = CoopSession.LocalPlayerId.Value,
                    Remaining = [],
                };

                Ledger[key] = message;
                Pending.Remove(key);
                Sent++;

                ReportLootTaken(message);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] reporting a pickup failed: " + e);
            }
        }

        internal static void ContainerStartPostfix(Interact_Bhvr_LootInventory __instance)
        {
            try
            {
                if (__instance == null) return;

                string? key;

                if (__instance._isActorLoot)
                {
                    // A body has no save root, so it is named by its creature, retried if not in the session yet.
                    if (!HookSwitches.IsOn(HookSwitches.LootBodies)) return;

                    key = BodyKeyOf(__instance);
                    if (key == null)
                    {
                        BodyRetryAt[__instance.GetInstanceID()] = _frame + BodyRetryFrames;
                        Bodies[__instance.GetInstanceID()] = __instance;
                        return;
                    }
                }
                else
                {
                    // Only a container the game itself filled.
                    Inventory? inventory = __instance._ownerInventory;
                    if (inventory == null) return;
                    if (inventory.GetComponent<Inventory_LootBhvr>() == null) return;

                    key = WorldObjects.KeyOf(__instance);
                    if (key == null) return;
                }

                if (NeverShared.Contains(key) || IsNeverSharedByGuid(__instance)) return;

                Bind(key, __instance);

                if (__instance._isActorLoot) BodiesNamed++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] registering a container failed: " + e);
            }
        }

        /// <summary>Files a container under its session name and queues an early change and its own offer.</summary>
        private static void Bind(string key, Interact_Bhvr_LootInventory container)
        {
            KeyByContainer[container.GetInstanceID()] = key;
            ContainerByKey[key] = container;

            // The host's own contents replace a client's offer, so that offer is not applied here.
            if (Pending.Contains(key) && !(Ledger.TryGetValue(key, out LootTakenMessage entry) && HostOutranks(entry)))
            {
                ApplyNow.Add(key);
            }

            if (!ProposeAt.ContainsKey(key)) ProposeAt[key] = _frame + ProposeDelayFrames;
        }

        /// <summary>Looks again at bodies whose creature was not part of the session when they spawned.</summary>
        private static void RetryBodies()
        {
            if (Bodies.Count == 0) return;

            BodyRetryNow.Clear();

            foreach (KeyValuePair<int, int> due in BodyRetryAt)
            {
                if (_frame >= due.Value) BodyRetryNow.Add(due.Key);
            }

            foreach (int instance in BodyRetryNow)
            {
                if (!Bodies.TryGetValue(instance, out Interact_Bhvr_LootInventory? body) || body == null)
                {
                    Bodies.Remove(instance);
                    BodyRetryAt.Remove(instance);
                    continue;
                }

                string? key = BodyKeyOf(body);

                if (key == null)
                {
                    int attempts = AttemptsByBody.GetValueOrDefault(instance) + 1;
                    AttemptsByBody[instance] = attempts;

                    if (attempts >= BodyAttempts)
                    {
                        Bodies.Remove(instance);
                        BodyRetryAt.Remove(instance);
                        AttemptsByBody.Remove(instance);
                        continue;
                    }

                    BodyRetryAt[instance] = _frame + BodyRetryFrames;
                    continue;
                }

                Bodies.Remove(instance);
                BodyRetryAt.Remove(instance);
                AttemptsByBody.Remove(instance);

                if (NeverShared.Contains(key) || IsNeverSharedByGuid(body)) continue;

                Bind(key, body);
                BodiesNamed++;
            }

            BodyRetryNow.Clear();
        }

        /// <summary>A body's loot key from its creature's session handle. Null when the creature is not in the session.</summary>
        private static string? BodyKeyOf(Interact_Bhvr_LootInventory container)
        {
            Actor? owner = container._ownerActor;
            if (owner == null) return null;

            if (!EnemyRegistry.TryGetSessionId(owner, out EnemyNetId id)) return null;

            return LootTakenMessage.CreatureKey(id.Value);
        }

        internal static void InventoryChangedPrefix(Interact_Bhvr_LootInventory __instance, out bool __state)
        {
            __state = __instance != null && __instance._inventoryOpened;
        }

        internal static void InventoryChangedPostfix(Interact_Bhvr_LootInventory __instance, InventoryChangedEventArgs arg0, bool __state)
        {
            try
            {
                if (__instance == null) return;
                if (arg0.IsInLoadingStage) return;
                if (RemoteApply<LootScope>.InProgress) return;
                if (!KeyByContainer.TryGetValue(__instance.GetInstanceID(), out string key)) return;

                // Only a change the player made with the window open counts. A closed-window change is the game's own.
                if (!__state) return;
                if (!RemotePlayers.IsLocalPlayer(__instance.CurrentActor)) return;

                if (arg0.Action == InventoryChangedEventArgs.EAction.Added)
                {
                    MakePrivate(key, "a player put something into it");
                    return;
                }

                if (arg0.Action != InventoryChangedEventArgs.EAction.Removed) return;
                if (PrivateKeys.Contains(key)) return;
                if (!CoopSession.IsLive || !Shared) return;

                LootTakenMessage message = new()
                {
                    RootKey = key,
                    Kind = LootTakenMessage.KindContainer,
                    Player = CoopSession.LocalPlayerId.Value,
                    Remaining = Snapshot(__instance.OwnerInventory),
                };

                Ledger[key] = message;
                Pending.Remove(key);
                Sent++;

                ReportLootTaken(message);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] reporting a container change failed: " + e);
            }
        }

        internal static void CreateLootInventoryPrefix(Interact_Bhvr_LootInventory __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!KeyByContainer.TryGetValue(__instance.GetInstanceID(), out string key)) return;
                if (!Pending.Contains(key)) return;

                TryApply(key);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] catching a container up before its window failed: " + e);
            }
        }

        internal static void TransferPostfix(Inventory from, Inventory to)
        {
            try
            {
                // Both directions, since the chest stays a story stash after the things come back out.
                MarkTransferEnd(from);
                MarkTransferEnd(to);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] noting a story inventory transfer failed: " + e);
            }
        }

        private static void MarkTransferEnd(Inventory? inventory)
        {
            if (inventory == null) return;
            if (PlayerAccess.TryGetPlayer(out Actor player))
            {
                Inventory? own = player.GetInventory();
                if (own != null && own.Pointer == inventory.Pointer) return;
            }

            string? key = WorldObjects.KeyOf(inventory);
            if (key != null)
            {
                MakePrivate(key, "the story moved a player's belongings through it");
            }
        }

        private static void MakePrivate(string key, string why)
        {
            if (!PrivateKeys.Add(key)) return;

            ContainerByKey.Remove(key);
            Pending.Remove(key);
            Ledger.Remove(key);

            MelonLogger.Msg($"[Loot] the container '{Complaints.Short(key)}' is no longer shared: {why}.");
        }

        /// <summary>Puts the ledger's state for one key into the local copy, if the copy is here and free.</summary>
        private static bool TryApply(string key)
        {
            if (!Pending.Contains(key)) return true;
            if (!Ledger.TryGetValue(key, out LootTakenMessage state)) return true;
            if (PrivateKeys.Contains(key)) return true;

            if (state.Kind == LootTakenMessage.KindPickup)
            {
                if (!PickupByKey.TryGetValue(key, out Saveable_PickUp_Once? pickup) || pickup == null) return false;

                using (RemoteApply<LootScope>.Enter())
                {
                    // The same two writes the game's load makes for a picked pickup.
                    if (pickup._saveData != null) pickup._saveData.WasPicked = true;

                    GameObject? root = pickup._pickUpRoot;
                    if (root != null) UnityEngine.Object.Destroy(root);
                }

                PickupByKey.Remove(key);
                Pending.Remove(key);
                Applied++;
                return true;
            }

            if (!ContainerByKey.TryGetValue(key, out Interact_Bhvr_LootInventory? container) || container == null) return false;

            // Not under an open window. The prefix on the next open catches it up.
            if (container._inventoryOpened) return false;

            IInventoryContainer? inventory = container.OwnerInventory;
            if (inventory == null) return false;

            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (database == null) return false;

            Inventory? staged = inventory.TryCast<Inventory>();

            using (RemoteApply<LootScope>.Enter())
            {
                // Refilled in the loading stage like a save restore, so no panel or sound fires.
                if (staged != null) staged.SetInLoadingStage(true);
                inventory.ClearInventory();

                foreach (LootStack stack in state.Remaining ?? [])
                {
                    if (string.IsNullOrEmpty(stack.Id) || stack.Amount <= 0) continue;

                    Item? item = database.GetItemByReadableId(stack.Id);
                    if (item == null)
                    {
                        MelonLogger.Warning($"[Loot] a teammate's chest held '{stack.Id}', which this game does not know. Mod sets may differ.");
                        continue;
                    }

                    inventory.AddItem(new ItemStack(item, stack.Amount), false, false);
                }

                if (staged != null) staged.SetInLoadingStage(false);
            }

            Pending.Remove(key);
            Applied++;
            return true;
        }

        private static LootStack[] Snapshot(IInventoryContainer? inventory)
        {
            if (inventory == null) return [];

            List<LootStack> stacks = new();

            foreach (ItemStack stack in inventory.InventoryItems)
            {
                if (stack == null || stack.Item == null || stack.Amount <= 0) continue;

                string id = stack.Item.ReadableId;
                if (string.IsNullOrEmpty(id)) continue;

                stacks.Add(new LootStack { Id = id, Amount = stack.Amount });
            }

            return [.. stacks];
        }

        /// <summary>Whether any guid component up to the scene root names one of the <see cref="NeverShared"/> chests.</summary>
        private static bool IsNeverSharedByGuid(Component behaviour)
        {
            Transform? node = behaviour.transform;

            while (node != null)
            {
                GuidComponent? guid = node.GetComponent<GuidComponent>();
                if (guid != null && NeverShared.Contains(guid.GuidString ?? string.Empty)) return true;

                node = node.parent;
            }

            return false;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<LootTakenMessage>(MessageIds.LootTaken, OnLootTaken);
            CoopSession.Register<LootSnapshotMessage>(MessageIds.LootSnapshot,
                (_, m) => CoopSession.QueueFromHost(m, IncomingLootSnapshot, MaxPendingLootSnapshots, "loot the session had already seen go"));
            CoopSession.Register<LootSnapshotEndMessage>(MessageIds.LootSnapshotEnd, OnLootSnapshotEnd);
        }

        void ICoopSystem.EndSession()
        {
            LootParts.Clear();
            IncomingLootSnapshot.Clear();
        }

        /// <returns>True when the message is now the whole container.</returns>
        private static bool Gather(INetPeer peer, ref LootTakenMessage message)
        {
            (int, string) key = (peer.Id, message.RootKey);
            if (LootParts.Remove(key, out LootStack[] earlier)) message = message with { Remaining = [.. earlier, .. message.Remaining] };

            if (!message.More) return true;

            if (message.Remaining.Length <= CoopSession.MaxGathered) LootParts[key] = message.Remaining;
            return false;
        }

        /// <summary>Reports a shared pickup or container looted here. See <see cref="LootShare"/>.</summary>
        private static void ReportLootTaken(in LootTakenMessage message)
        {
            foreach (LootTakenMessage part in message.Split()) CoopSession.SendIfLive(part);
        }

        /// <summary>Takes the next chunk of the loot snapshot a joining client was sent.</summary>
        private static bool TryTakeLootSnapshot(out LootSnapshotMessage message)
            => IncomingLootSnapshot.TryDequeue(out message);

        private static void OnLootTaken(INetPeer peer, LootTakenMessage message)
        {
            if (!message.IsPlausible()) return;
            if (!Gather(peer, ref message)) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (CoopSession.State != CoopSessionState.Hosting) return;

                // Relayed only under the shared rule: a client sharing on its own has no claim on other chests.
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;
                if (CoopSession.LootMode != CoopProtocol.LootModeShared) return;

                message = message with { Player = sender.Value };

                if (message.Kind == LootTakenMessage.KindProposal)
                {
                    // First offer per chest stands. Only the loser is told which one does.
                    if (!TryAcceptProposal(message, out LootTakenMessage? standing))
                    {
                        if (standing.HasValue)
                        {
                            foreach (LootTakenMessage part in standing.Value.Split()) NetworkAccess.Send(peer, part);
                        }

                        return;
                    }

                    foreach (LootTakenMessage part in message.Split()) CoopSession.SendToClientsExcept(part, peer.Id);
                    return;
                }

                Park(message);
                foreach (LootTakenMessage part in message.Split()) CoopSession.SendToClientsExcept(part, peer.Id);
                return;
            }

            if (!CoopSession.IsFromHost()) return;

            Park(message);
        }

        private static void OnLootSnapshotEnd(INetPeer peer, LootSnapshotEndMessage message)
        {
            if (!CoopSession.IsFromHost()) return;

            CompleteSnapshot(message.Count);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
