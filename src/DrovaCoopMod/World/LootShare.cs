using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.DialogueNew;
using Il2CppDrova.InteractionSystem;
using Il2CppDrova.InventorySystem;
using Il2CppDrova.Items;
using Il2CppDrova.Saveables;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// One world's worth of loot for the party, when the host's loot rule says so.
    ///
    /// Every machine holds its own copy of every chest and pickup, filled by its own game, so without
    /// this two players walking the same road each take the same purse. Under the shared rule a pickup
    /// taken here is removed everywhere, and a container looted here has its far copies set to what was
    /// left in it. The remainder crosses rather than the take, because the copies never held the same
    /// things: a chest rolls its loot when it spawns, per game. The first player to open a chest decides
    /// what it held.
    ///
    /// Only loot the game itself put there takes part. A container is shared when its inventory carries
    /// an <c>Inventory_LootBhvr</c>, which is the component that fills it from a loot table; a chest a
    /// player stores things in has none, and neither do the two chests the story stows a captured
    /// player's belongings in. Those two are also named by guid, and any inventory a dialogue transfers
    /// the player's things into is marked off for the session, so a chest of somebody's own gear can
    /// never be overwritten by a teammate's leftovers. A player putting something into a shared chest
    /// takes it out of sharing for the same reason.
    ///
    /// A far copy that is not loaded is caught up when it loads, and a chest the far player has open at
    /// the moment is caught up when they open it next, because tearing items out from under the open
    /// window is what the game logs errors about. The host keeps everything that went this session and
    /// hands it to a joining player. Nothing here reaches a savegame except through the game's own
    /// copy: a pickup is destroyed and marked picked the way a load would, a chest is refilled the way a
    /// load would, and the save writes what it finds.
    ///
    /// Not shared: corpses and knocked-out bodies, whose loot lives under a trigger the game spawns at the
    /// scene root with no save root above it, and pickups a cache spawns, which are numbered per game.
    /// </summary>
    internal static class LootShare
    {
        /// <summary>
        /// Chests a capture sequence stows the player's entire inventory in. By scene guid, because
        /// they carry no loot behavior either way; this is the belt to that suspender. The first is the
        /// Lothar capture, the second the bandit mine.
        /// </summary>
        private static readonly HashSet<string> NeverShared = new(StringComparer.OrdinalIgnoreCase)
        {
            "1f3320bf-b5bc-472c-af8a-06f331b9ccb0",
            "3d8311f8-6bed-4232-abb8-9c11df2f17ed",
        };

        private const int ApplyBudgetPerFrame = 8;

        private static readonly Dictionary<string, Interact_Bhvr_LootInventory> _containerByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<int, string> _keyByContainer = new();

        private static readonly Dictionary<string, Saveable_PickUp_Once> _pickupByKey = new(StringComparer.Ordinal);

        private static readonly Dictionary<int, string> _keyByPickup = new();

        /// <summary>Everything shared loot has seen go this session, by key. The host's copy is what a joiner gets.</summary>
        private static readonly Dictionary<string, LootTakenMessage> _ledger = new(StringComparer.Ordinal);

        /// <summary>Keys whose latest state has not reached the local copy yet.</summary>
        private static readonly HashSet<string> _pending = new(StringComparer.Ordinal);

        /// <summary>Inventories a player's own things were moved into, or that a player deposited into. Never shared again this session.</summary>
        private static readonly HashSet<string> _privateKeys = new(StringComparer.Ordinal);

        private static readonly List<string> _applyNow = new();

        private static int _hooksInstalled;

        private static int _sent;

        private static int _applied;

        private static int _snapshotReceived;

        private static bool _snapshotComplete;

        /// <summary>How many of the six patches applied. Six is the number that means the feature works.</summary>
        internal static int HooksInstalled => _hooksInstalled;

        /// <summary>How many takes this machine told the session about.</summary>
        internal static int Sent => _sent;

        /// <summary>How many remote takes this machine applied to its own world.</summary>
        internal static int Applied => _applied;

        /// <summary>How many remote takes are still waiting for their object to load, or their window to close.</summary>
        internal static int Held => _pending.Count;

        /// <summary>How many pieces of loot the session has seen go, on this machine's ledger.</summary>
        internal static int Known => _ledger.Count;

        /// <summary>How many containers and pickups are loaded here under a key a teammate could name.</summary>
        internal static int Addressable => _containerByKey.Count + _pickupByKey.Count;

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            // A pickup announcing itself, on load and on spawn, so a take that arrived first can land.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_PickUp_Once), nameof(Saveable_PickUp_Once.Start),
                typeof(LootShare), nameof(PickupStartPostfix))) applied++;

            // The take of a pickup. The same funnel HaulShare listens on; two postfixes on one method
            // are fine, and only one of them will recognize any given root.
            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_LootAll), nameof(Interact_Bhvr_LootAll.LootAll),
                typeof(LootShare), nameof(LootAllPostfix))) applied++;

            // A container announcing itself.
            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_LootInventory), nameof(Interact_Bhvr_LootInventory.Start),
                typeof(LootShare), nameof(ContainerStartPostfix))) applied++;

            // Something leaving or entering an open container. A listener, so its address is taken and
            // it cannot have been inlined.
            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_LootInventory), nameof(Interact_Bhvr_LootInventory.InventoryChangedEventListener),
                typeof(LootShare), nameof(InventoryChangedPostfix))) applied++;

            // The moment before the loot window is laid out from the inventory, which is the one place
            // a held remainder can be put in without the window showing slots that are no longer there.
            if (Hooking.TryPrefix(harmony, typeof(Interact_Bhvr_LootInventory), nameof(Interact_Bhvr_LootInventory.CreateLootInventory),
                typeof(LootShare), nameof(CreateLootInventoryPrefix))) applied++;

            // The story moving a player's belongings into a chest, or back out of one.
            if (Hooking.TryPostfix(harmony, typeof(DS_TransferInventoryNode), nameof(DS_TransferInventoryNode.Transferring),
                typeof(LootShare), nameof(TransferPostfix))) applied++;

            _hooksInstalled = applied;

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

        /// <summary>
        /// Forgets everything session-scoped. On leaving a session and on a savegame load inside one,
        /// since either makes the ledger describe a world this machine no longer has.
        /// </summary>
        internal static void Reset()
        {
            _ledger.Clear();
            _pending.Clear();
            _privateKeys.Clear();
            _applyNow.Clear();
            _snapshotReceived = 0;
            _snapshotComplete = false;
            _sent = 0;
            _applied = 0;
        }

        /// <summary>
        /// Drains the join snapshot and applies whatever became applicable. Called once per frame in
        /// gameplay.
        /// </summary>
        internal static void Update()
        {
            int budget = ApplyBudgetPerFrame;

            while (budget-- > 0 && CoopSession.TryTakeLootSnapshot(out LootSnapshotMessage chunk))
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
                    $"arrived; {_pending.Count} wait for their part of the world.");
            }

            if (_applyNow.Count == 0) return;

            foreach (string key in _applyNow)
            {
                TryApply(key);
            }

            _applyNow.Clear();
        }

        /// <summary>
        /// Marks the snapshot complete, so the next update can say how much of it arrived.
        /// </summary>
        internal static void CompleteSnapshot(ushort count)
        {
            _snapshotComplete = true;

            if (count != _snapshotReceived)
            {
                MelonLogger.Warning($"[Loot] the host announced {count} piece(s) of loot and {_snapshotReceived} arrived.");
            }
        }

        /// <summary>
        /// Sends a joining player everything the ledger holds. Host only.
        /// </summary>
        internal static void SendTo(INetPeer peer)
        {
            try
            {
                List<LootSnapshotMessage.Record> records = new();
                int sent = 0;

                foreach (LootTakenMessage entry in _ledger.Values)
                {
                    records.Add(new LootSnapshotMessage.Record
                    {
                        RootKey = entry.RootKey,
                        Kind = entry.Kind,
                        Remaining = entry.Remaining ?? [],
                    });

                    if (records.Count >= LootSnapshotMessage.MaxRecords)
                    {
                        NetworkAccess.Send(peer, new LootSnapshotMessage { Records = [.. records] });
                        sent += records.Count;
                        records.Clear();
                    }
                }

                if (records.Count > 0)
                {
                    NetworkAccess.Send(peer, new LootSnapshotMessage { Records = [.. records] });
                    sent += records.Count;
                }

                NetworkAccess.Send(peer, new LootSnapshotEndMessage { Count = (ushort)Math.Min(sent, ushort.MaxValue) });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] sending the loot ledger to a joining player failed: " + e);
            }
        }

        /// <summary>
        /// Takes in a take from the session, on any machine. Recorded, then applied now if the object is
        /// here and free, else held for it.
        /// </summary>
        internal static void Park(in LootTakenMessage message)
        {
            if (!message.IsPlausible()) return;

            _ledger[message.RootKey] = message;
            _pending.Add(message.RootKey);
            _applyNow.Add(message.RootKey);
        }

        private static bool Shared => CoopSession.LootMode == CoopProtocol.LootModeShared;

        private static bool InSession => CoopSession.State == CoopSessionState.Hosting || CoopSession.State == CoopSessionState.Joined;

        private static void PickupStartPostfix(Saveable_PickUp_Once __instance)
        {
            try
            {
                if (__instance == null) return;

                string? key = KeyOf(__instance);
                if (key == null) return;

                _keyByPickup[__instance.GetInstanceID()] = key;
                _pickupByKey[key] = __instance;

                if (_pending.Contains(key)) _applyNow.Add(key);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] registering a pickup failed: " + e);
            }
        }

        private static void LootAllPostfix(Interact_Bhvr_LootAll __instance, Actor actor)
        {
            try
            {
                if (!InSession || !Shared) return;
                if (RemoteApply<LootScope>.InProgress) return;
                if (__instance == null || !IsLocalPlayer(actor)) return;

                Saveable_PickUp_Once? pickup = __instance.GetComponent<Saveable_PickUp_Once>();
                if (pickup == null) pickup = __instance.GetComponentInParent<Saveable_PickUp_Once>();
                if (pickup == null) return;

                if (!_keyByPickup.TryGetValue(pickup.GetInstanceID(), out string key)) return;

                LootTakenMessage message = new()
                {
                    RootKey = key,
                    Kind = LootTakenMessage.KindPickup,
                    Player = CoopSession.LocalPlayerId.Value,
                    Remaining = [],
                };

                _ledger[key] = message;
                _pending.Remove(key);
                _sent++;

                CoopSession.ReportLootTaken(message);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] reporting a pickup failed: " + e);
            }
        }

        private static void ContainerStartPostfix(Interact_Bhvr_LootInventory __instance)
        {
            try
            {
                if (__instance == null) return;

                // Only a container the game itself filled. A storage chest and the story's transfer
                // chests carry no loot behavior, and a knocked-out body has no owner inventory at all.
                Inventory? inventory = __instance._ownerInventory;
                if (inventory == null) return;
                if (inventory.GetComponent<Inventory_LootBhvr>() == null) return;

                string? key = KeyOf(__instance);
                if (key == null) return;

                if (NeverShared.Contains(key) || IsNeverSharedByGuid(__instance)) return;

                _keyByContainer[__instance.GetInstanceID()] = key;
                _containerByKey[key] = __instance;

                if (_pending.Contains(key)) _applyNow.Add(key);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] registering a container failed: " + e);
            }
        }

        private static void InventoryChangedPostfix(Interact_Bhvr_LootInventory __instance, InventoryChangedEventArgs arg0)
        {
            try
            {
                if (__instance == null) return;
                if (arg0.IsInLoadingStage) return;
                if (RemoteApply<LootScope>.InProgress) return;
                if (!_keyByContainer.TryGetValue(__instance.GetInstanceID(), out string key)) return;

                // Only a change the player made with the window open counts. A change while it is closed
                // is the game's own doing: a load, a script, a respawn.
                if (!__instance._inventoryOpened) return;
                if (!IsLocalPlayer(__instance.CurrentActor)) return;

                if (arg0.Action == InventoryChangedEventArgs.EAction.Added)
                {
                    // The player's own things are in it now. From here on it is theirs, not the party's.
                    MakePrivate(key, "a player put something into it");
                    return;
                }

                if (arg0.Action != InventoryChangedEventArgs.EAction.Removed) return;
                if (_privateKeys.Contains(key)) return;
                if (!InSession || !Shared) return;

                LootTakenMessage message = new()
                {
                    RootKey = key,
                    Kind = LootTakenMessage.KindContainer,
                    Player = CoopSession.LocalPlayerId.Value,
                    Remaining = Snapshot(__instance.OwnerInventory),
                };

                _ledger[key] = message;
                _pending.Remove(key);
                _sent++;

                CoopSession.ReportLootTaken(message);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] reporting a container change failed: " + e);
            }
        }

        private static void CreateLootInventoryPrefix(Interact_Bhvr_LootInventory __instance)
        {
            try
            {
                if (__instance == null) return;
                if (!_keyByContainer.TryGetValue(__instance.GetInstanceID(), out string key)) return;
                if (!_pending.Contains(key)) return;

                TryApply(key);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] catching a container up before its window failed: " + e);
            }
        }

        private static void TransferPostfix(Inventory from, Inventory to)
        {
            try
            {
                // Whichever end is not the player is a chest holding, or having held, the player's own
                // things. Marked in both directions: after the things come back out, the chest is still
                // one the story uses for that, and a teammate's remainder must never land in it.
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
            if (PlayerAccess.TryGetPlayer(out Actor player) && player != null && player.GetInventory()?.Pointer == inventory.Pointer) return;

            string? key = KeyOf(inventory);
            if (key != null)
            {
                MakePrivate(key, "the story moved a player's belongings through it");
            }
        }

        private static void MakePrivate(string key, string why)
        {
            if (!_privateKeys.Add(key)) return;

            _containerByKey.Remove(key);
            _pending.Remove(key);
            _ledger.Remove(key);

            MelonLogger.Msg($"[Loot] the container '{Short(key)}' is no longer shared: {why}.");
        }

        /// <summary>
        /// Puts the ledger's state for one key into the local copy, if the copy is here and free.
        /// </summary>
        private static bool TryApply(string key)
        {
            if (!_pending.Contains(key)) return true;
            if (!_ledger.TryGetValue(key, out LootTakenMessage state)) return true;
            if (_privateKeys.Contains(key)) return true;

            if (state.Kind == LootTakenMessage.KindPickup)
            {
                if (!_pickupByKey.TryGetValue(key, out Saveable_PickUp_Once pickup) || pickup == null) return false;

                using (RemoteApply<LootScope>.Enter())
                {
                    // The same two writes the game's own load makes for a picked pickup, so the save
                    // records it the same way and the object leaves the same way.
                    if (pickup._saveData != null) pickup._saveData.WasPicked = true;

                    GameObject? root = pickup._pickUpRoot;
                    if (root != null) UnityEngine.Object.Destroy(root);
                }

                _pickupByKey.Remove(key);
                _pending.Remove(key);
                _applied++;
                return true;
            }

            if (!_containerByKey.TryGetValue(key, out Interact_Bhvr_LootInventory container) || container == null) return false;

            // Not under an open window; the prefix on the next open catches it up instead.
            if (container._inventoryOpened) return false;

            Inventory? inventory = container._ownerInventory;
            if (inventory == null) return false;

            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (database == null) return false;

            using (RemoteApply<LootScope>.Enter())
            {
                // The way the game's own save restores a container: cleared and refilled in the loading
                // stage, so nothing on the information panel or the sound side notices.
                inventory.SetInLoadingStage(true);
                inventory.ClearInventory();

                foreach (LootStack stack in state.Remaining ?? [])
                {
                    if (string.IsNullOrEmpty(stack.Id) || stack.Amount <= 0) continue;

                    Item item = database.GetItemByReadableId(stack.Id);
                    if (item == null)
                    {
                        MelonLogger.Warning($"[Loot] a teammate's chest held '{stack.Id}', which this game does not know. Mod sets may differ.");
                        continue;
                    }

                    inventory.AddItem(new ItemStack(item, stack.Amount), false, false);
                }

                inventory.SetInLoadingStage(false);
            }

            _pending.Remove(key);
            _applied++;
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
                if (stacks.Count >= LootTakenMessage.MaxStacks) break;
            }

            return [.. stacks];
        }

        /// <summary>
        /// The save root key of a scene object, or null when no teammate could resolve it: nothing
        /// above it saves itself, or it saves under a dynamic root numbered per savegame.
        /// </summary>
        private static string? KeyOf(Component behaviour)
        {
            ASaveRoot? root = behaviour.GetComponent<ASaveRoot>();
            if (root == null) root = behaviour.GetComponentInParent<ASaveRoot>();
            if (root == null) return null;

            SaveRoot_Dynamic? dynamic = root.TryCast<SaveRoot_Dynamic>();
            if (dynamic != null && dynamic.IsDynamic()) return null;

            string key = root.GetGameObjectInstanceKey();
            return ResourceWorkMessage.IsReplicableKey(key) ? key : null;
        }

        /// <summary>
        /// Whether any guid component between the container and the scene root names one of the chests
        /// that hold a captured player's belongings. The save root key is usually that same guid, but
        /// the chain is what the guids were written against.
        /// </summary>
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

        private static bool IsLocalPlayer(Actor? actor)
        {
            if (actor == null) return false;

            return PlayerAccess.TryGetPlayer(out Actor player) && player != null && player.Pointer == actor.Pointer;
        }

        private static string Short(string key)
        {
            return key.Length > 8 ? key[..8] : key;
        }
    }
}
