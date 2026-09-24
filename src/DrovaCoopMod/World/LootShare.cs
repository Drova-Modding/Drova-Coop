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
    /// things: a chest rolls its loot when it spawns, per game.
    ///
    /// Which roll counts is settled before anyone opens the chest. A few frames after a shared container
    /// streams in, the machine that has it offers its contents to the session; the host keeps the first
    /// offer per chest and every other copy is set to it. The host's own copy outranks a client's offer,
    /// because the host's save is the world, so a chest the host's save had emptied stays empty for the
    /// party. The delay exists because the game restores a chest's saved contents from the save root's
    /// own <c>Start</c>, and the order of two <c>Start</c>s in one frame is not promised. Without the
    /// offer, two players opening a chest at once each took from their own roll and both takes stood.
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

        /// <summary>
        /// Frames between a container registering and its contents being offered. The save root that
        /// restores a visited chest's contents does so from its own <c>Start</c>, which may run after the
        /// container's in the same frame; a handful of frames later the restore has run either way.
        /// </summary>
        private const int ProposeDelayFrames = 5;

        /// <summary>
        /// Frames before a container that was open when its turn came is looked at again.
        /// </summary>
        private const int ProposeRetryFrames = 30;

        /// <summary>
        /// The most offers one frame sends. When a session begins with a town's worth of chests loaded,
        /// every one of them is due at once.
        /// </summary>
        private const int ProposeBudgetPerFrame = 4;

        /// <summary>Frames between two looks at a body whose creature was not named yet.</summary>
        private const int BodyRetryFrames = 15;

        /// <summary>
        /// How many looks a body gets before it is left to this world alone. Enough to outlast a death
        /// crossing the wire; not so many that a creature outside the session is watched forever.
        /// </summary>
        private const int BodyAttempts = 20;

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

        /// <summary>Containers whose contents are still to be offered, by key, with the frame to do it in.</summary>
        private static readonly Dictionary<string, int> _proposeAt = new(StringComparer.Ordinal);

        private static readonly List<string> _proposeNow = new();

        /// <summary>Bodies whose creature was not part of the session yet, by instance id.</summary>
        private static readonly Dictionary<int, Interact_Bhvr_LootInventory> _bodies = new();

        /// <summary>When each of those is worth another look, as a reading of <see cref="_frame"/>.</summary>
        private static readonly Dictionary<int, int> _bodyRetryAt = new();

        private static readonly List<int> _bodyRetryNow = new();

        /// <summary>How many times each waiting body has been looked at.</summary>
        private static readonly Dictionary<int, int> _bodyAttempts = new();

        private static int _frame;

        private static bool _snapshotDone;

        private static int _snapshotReceived;

        private static bool _snapshotComplete;

        /// <summary>How many of the six patches applied. Six is the number that means the feature works.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How many takes this machine told the session about.</summary>
        internal static int Sent { get; private set; }

        /// <summary>How many remote takes this machine applied to its own world.</summary>
        internal static int Applied { get; private set; }

        /// <summary>How many remote takes are still waiting for their object to load, or their window to close.</summary>
        internal static int Held => _pending.Count;

        /// <summary>How many pieces of loot the session has seen go, on this machine's ledger.</summary>
        internal static int Known => _ledger.Count;

        /// <summary>How many containers and pickups are loaded here under a key a teammate could name.</summary>
        internal static int Addressable => _containerByKey.Count + _pickupByKey.Count;

        /// <summary>How many containers this machine offered the contents of.</summary>
        internal static int Proposed { get; private set; }

        /// <summary>Host only: how many offers lost to an entry that already stood, or to the host's own copy.</summary>
        internal static int ProposalsRefused { get; private set; }

        /// <summary>How many corpses and knocked-out bodies this machine named by their creature.</summary>
        internal static int BodiesNamed { get; private set; }

        /// <summary>How many bodies are still waiting for their creature to be part of the session.</summary>
        internal static int BodiesWaiting => _bodies.Count;

        private static bool IsHost => CoopSession.State == CoopSessionState.Hosting;

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
            _snapshotDone = false;
            Sent = 0;
            Applied = 0;
            Proposed = 0;
            ProposalsRefused = 0;
            BodiesNamed = 0;

            // A body is named by a session handle, and the handles do not survive the session that
            // handed them out. Anything still waiting is waiting on a name that will never come.
            _bodies.Clear();
            _bodyRetryAt.Clear();
            _bodyAttempts.Clear();
            _bodyRetryNow.Clear();

            // A creature key names a session handle, and handles are handed out again from zero by the
            // next session. Left in place, a corpse from the last session would answer to whatever
            // creature 0 is in the next one - both for an offer this machine sends and for a take it
            // receives. Save root keys mean the same thing in every session and stay.
            foreach (string key in _containerByKey.Keys.Where(LootTakenMessage.IsCreatureKey).ToList())
            {
                if (_containerByKey.TryGetValue(key, out Interact_Bhvr_LootInventory? body) && body != null)
                {
                    _keyByContainer.Remove(body.GetInstanceID());
                }

                _containerByKey.Remove(key);
                _proposeAt.Remove(key);
            }

            // Everything still loaded here is unoffered again: the ledger that knew about it is gone, and
            // the next session has to be told what these chests hold.
            foreach (string key in _containerByKey.Keys)
            {
                _proposeAt[key] = _frame + ProposeDelayFrames;
            }
        }

        /// <summary>
        /// Drains the join snapshot and applies whatever became applicable. Called once per frame in
        /// gameplay.
        /// </summary>
        internal static void Update()
        {
            _frame++;
            RetryBodies();
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

            if (_applyNow.Count > 0)
            {
                foreach (string key in _applyNow)
                {
                    TryApply(key);
                }

                _applyNow.Clear();
            }

            ProposeDue();
        }

        /// <summary>
        /// Offers the contents of every container whose delay has run out. A client waits for the host's
        /// ledger first, or every chest in view would be offered and refused on joining.
        /// </summary>
        private static void ProposeDue()
        {
            if (_proposeAt.Count == 0) return;
            if (!CoopSession.IsLive || !Shared) return;
            if (!IsHost && !_snapshotDone) return;

            _proposeNow.Clear();

            foreach (KeyValuePair<string, int> due in _proposeAt)
            {
                if (due.Value > _frame) continue;

                _proposeNow.Add(due.Key);
                if (_proposeNow.Count >= ProposeBudgetPerFrame) break;
            }

            foreach (string key in _proposeNow)
            {
                _proposeAt.Remove(key);

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
        /// Offers one container's contents, unless the session already knows the chest or the chest is
        /// open here. Open, a take from the window decides it instead. On the host, an entry that is a
        /// client's offer is replaced: the host's copy is the world's.
        /// </summary>
        private static void Propose(string key)
        {
            if (_privateKeys.Contains(key)) return;
            if (!_containerByKey.TryGetValue(key, out Interact_Bhvr_LootInventory container) || container == null) return;
            if (_ledger.TryGetValue(key, out LootTakenMessage existing) && !HostOutranks(existing)) return;

            // Open here right now. A take from the window will decide it; if none comes, it is looked at
            // again once the window is closed, so a chest opened and left alone is still offered.
            if (container._inventoryOpened)
            {
                _proposeAt[key] = _frame + ProposeRetryFrames;
                return;
            }

            // Through the accessor, not the serialized field. A body's loot lives in the container the
            // game builds when the creature dies and hands to Init; its _ownerInventory stays null, so
            // reading the field directly refuses every corpse.
            if (container.OwnerInventory == null) return;

            LootTakenMessage message = new()
            {
                RootKey = key,
                Kind = LootTakenMessage.KindProposal,
                Player = CoopSession.LocalPlayerId.Value,
                Remaining = Snapshot(container.OwnerInventory),
            };

            _ledger[key] = message;
            _pending.Remove(key);
            Proposed++;

            CoopSession.ReportLootTaken(message);
        }

        /// <summary>
        /// Whether this machine's copy outranks the entry that stands: only the host's does, and only over
        /// a client's offer. A take is a take wherever it happened.
        /// </summary>
        private static bool HostOutranks(in LootTakenMessage entry)
        {
            return IsHost && entry.Kind == LootTakenMessage.KindProposal && entry.Player != PlayerId.Host.Value;
        }

        /// <summary>
        /// Host only: decides a client's offer. True when it is adopted and should go to the others.
        /// False when an entry already stands, which is handed back to answer the sender with, or when
        /// the host's own copy is loaded, in which case the host's own offer follows and there is nothing
        /// to answer yet.
        /// </summary>
        internal static bool TryAcceptProposal(in LootTakenMessage message, out LootTakenMessage? answer)
        {
            answer = null;
            if (!message.IsPlausible()) return false;

            if (_ledger.TryGetValue(message.RootKey, out LootTakenMessage existing))
            {
                ProposalsRefused++;
                answer = existing;
                return false;
            }

            if (_containerByKey.ContainsKey(message.RootKey) || _privateKeys.Contains(message.RootKey))
            {
                ProposalsRefused++;
                return false;
            }

            Park(message);
            return true;
        }

        /// <summary>
        /// Marks the snapshot complete, so the next update can say how much of it arrived.
        /// </summary>
        internal static void CompleteSnapshot(ushort count)
        {
            _snapshotComplete = true;
            _snapshotDone = true;

            if (count != _snapshotReceived)
            {
                MelonLogger.Warning($"[Loot] the host announced {count} piece(s) of loot and {_snapshotReceived} arrived.");
            }
        }

        /// <summary>
        /// The pickups the party has taken, for the savegame to remember. Only pickups: a container is
        /// settled by the offer both machines make when it streams in, so carrying chests across a
        /// restart would be writing down what the world works out on its own.
        /// </summary>
        internal static IEnumerable<string> ExportTakenPickups()
        {
            foreach (KeyValuePair<string, LootTakenMessage> entry in _ledger)
            {
                if (entry.Value.Kind != LootTakenMessage.KindPickup) continue;

                // Only a name a save can mean again next time. A body is named by a session handle, and
                // handles are handed out afresh by the next session.
                if (LootTakenMessage.IsCreatureKey(entry.Key)) continue;

                yield return entry.Key;
            }
        }

        /// <summary>
        /// Puts a pickup this world took in an earlier session back on the ledger, so a joiner is told
        /// about it. Applied like any other entry once the copy is loaded here.
        /// </summary>
        /// <returns>False when the key is not one a teammate could act on.</returns>
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
            if (_ledger.ContainsKey(rootKey)) return false;

            // Not marked pending. What is pending is a change waiting for this world's copy of the thing
            // to load, and the host's own copy of a remembered pickup is already gone - that is why the
            // savegame remembers it. Marking it would leave the key waiting for an object that will never
            // register, and the waiting count is a number somebody reads.
            _ledger[rootKey] = entry;
            return true;
        }

        /// <summary>
        /// Sends a joining player everything the ledger holds. Host only.
        /// </summary>
        internal static void SendTo(INetPeer peer)
        {
            try
            {
                LootSnapshotMessage.Record[] records = _ledger.Values
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

        private static void PickupStartPostfix(Saveable_PickUp_Once __instance)
        {
            try
            {
                if (__instance == null) return;

                // A pickup a cache spilled carries a guid it minted itself a moment ago, which has the
                // shape of a real one and the meaning of nothing: no other machine holds it, and every
                // report naming it is noise the ledger and now the savegame would keep. Caches are shared
                // as caches instead; see CacheShare.
                if (CacheShare.WasSpilled(__instance)) return;

                string? key = WorldObjects.KeyOf(__instance);
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
                if (!CoopSession.IsLive || !Shared) return;
                if (RemoteApply<LootScope>.InProgress) return;
                if (__instance == null || !RemotePlayers.IsLocalPlayer(actor)) return;

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
                Sent++;

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

                string? key;

                if (__instance._isActorLoot)
                {
                    // A body. The game spawns its loot trigger when the creature dies and parents it to
                    // the corpse, so it has no save root of its own and the creature is the only name
                    // both machines share. Registration can be early - the body may not be part of the
                    // session yet - so a failure here is retried rather than dropped.
                    if (!HookSwitches.IsOn(HookSwitches.LootBodies)) return;

                    key = BodyKeyOf(__instance);
                    if (key == null)
                    {
                        _bodyRetryAt[__instance.GetInstanceID()] = _frame + BodyRetryFrames;
                        _bodies[__instance.GetInstanceID()] = __instance;
                        return;
                    }
                }
                else
                {
                    // Only a container the game itself filled. A storage chest and the story's transfer
                    // chests carry no loot behavior.
                    Inventory? inventory = __instance._ownerInventory;
                    if (inventory == null) return;
                    if (inventory.GetComponent<Inventory_LootBhvr>() == null) return;

                    key = WorldObjects.KeyOf(__instance);
                    if (key == null) return;
                }

                if (NeverShared.Contains(key) || IsNeverSharedByGuid(__instance)) return;

                Bind(key, __instance);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Loot] registering a container failed: " + e);
            }
        }

        /// <summary>
        /// Files a container or a body under the name the session knows it by, and queues whatever is
        /// owed to it: a change that arrived before it loaded, and its own offer of what it holds.
        /// </summary>
        private static void Bind(string key, Interact_Bhvr_LootInventory container)
        {
            _keyByContainer[container.GetInstanceID()] = key;
            _containerByKey[key] = container;

            // Not applied when it is a client's offer and this is the host: the host's own contents
            // are about to replace it, and applying first would replace them with the offer.
            if (_pending.Contains(key) && !(_ledger.TryGetValue(key, out LootTakenMessage entry) && HostOutranks(entry)))
            {
                _applyNow.Add(key);
            }

            if (!_proposeAt.ContainsKey(key)) _proposeAt[key] = _frame + ProposeDelayFrames;
        }

        /// <summary>
        /// Looks again at bodies whose creature was not part of the session when they spawned. A corpse
        /// appears the moment the creature dies, and the handle it is named by can be a beat behind.
        /// </summary>
        private static void RetryBodies()
        {
            if (_bodies.Count == 0) return;

            _bodyRetryNow.Clear();

            foreach (KeyValuePair<int, int> due in _bodyRetryAt)
            {
                if (_frame >= due.Value) _bodyRetryNow.Add(due.Key);
            }

            foreach (int instance in _bodyRetryNow)
            {
                if (!_bodies.TryGetValue(instance, out Interact_Bhvr_LootInventory? body) || body == null)
                {
                    _bodies.Remove(instance);
                    _bodyRetryAt.Remove(instance);
                    continue;
                }

                string? key = BodyKeyOf(body);

                if (key == null)
                {
                    // Given up on rather than retried forever. A creature that is still nameless this
                    // long after dying is one the session never held, and its corpse is this world's
                    // own business.
                    int attempts = _bodyAttempts.GetValueOrDefault(instance) + 1;
                    _bodyAttempts[instance] = attempts;

                    if (attempts >= BodyAttempts)
                    {
                        _bodies.Remove(instance);
                        _bodyRetryAt.Remove(instance);
                        _bodyAttempts.Remove(instance);
                        continue;
                    }

                    _bodyRetryAt[instance] = _frame + BodyRetryFrames;
                    continue;
                }

                _bodies.Remove(instance);
                _bodyRetryAt.Remove(instance);
                _bodyAttempts.Remove(instance);

                if (NeverShared.Contains(key) || IsNeverSharedByGuid(body)) continue;

                Bind(key, body);
                BodiesNamed++;
            }

            _bodyRetryNow.Clear();
        }

        /// <summary>
        /// The name a body's loot is known by: the handle the session holds for whatever died. Null when
        /// the creature is not part of the session, which is the ordinary answer for anything that was
        /// never claimed.
        /// </summary>
        private static string? BodyKeyOf(Interact_Bhvr_LootInventory container)
        {
            Actor? owner = container._ownerActor;
            if (owner == null) return null;

            if (!EnemyRegistry.TryGetSessionId(owner, out Messages.EnemyNetId id)) return null;

            return LootTakenMessage.CreatureKey(id.Value);
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
                if (!RemotePlayers.IsLocalPlayer(__instance.CurrentActor)) return;

                if (arg0.Action == InventoryChangedEventArgs.EAction.Added)
                {
                    // The player's own things are in it now. From here on it is theirs, not the party's.
                    MakePrivate(key, "a player put something into it");
                    return;
                }

                if (arg0.Action != InventoryChangedEventArgs.EAction.Removed) return;
                if (_privateKeys.Contains(key)) return;
                if (!CoopSession.IsLive || !Shared) return;

                LootTakenMessage message = new()
                {
                    RootKey = key,
                    Kind = LootTakenMessage.KindContainer,
                    Player = CoopSession.LocalPlayerId.Value,
                    Remaining = Snapshot(__instance.OwnerInventory),
                };

                _ledger[key] = message;
                _pending.Remove(key);
                Sent++;

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
            if (PlayerAccess.TryGetPlayer(out Actor player) && player.GetInventory()?.Pointer == inventory.Pointer) return;

            string? key = WorldObjects.KeyOf(inventory);
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

            MelonLogger.Msg($"[Loot] the container '{Complaints.Short(key)}' is no longer shared: {why}.");
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
                Applied++;
                return true;
            }

            if (!_containerByKey.TryGetValue(key, out Interact_Bhvr_LootInventory container) || container == null) return false;

            // Not under an open window; the prefix on the next open catches it up instead.
            if (container._inventoryOpened) return false;

            IInventoryContainer? inventory = container.OwnerInventory;
            if (inventory == null) return false;

            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (database == null) return false;

            // The loading stage belongs to the concrete inventory rather than to the interface, and a
            // body's runtime container is one in practice. Nothing is lost when it is not: the stage
            // only suppresses the panel and the sound.
            Inventory? staged = inventory.TryCast<Inventory>();

            using (RemoteApply<LootScope>.Enter())
            {
                // The way the game's own save restores a container: cleared and refilled in the loading
                // stage, so nothing on the information panel or the sound side notices.
                staged?.SetInLoadingStage(true);
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

                staged?.SetInLoadingStage(false);
            }

            _pending.Remove(key);
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
                if (stacks.Count >= LootTakenMessage.MaxStacks) break;
            }

            return [.. stacks];
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
    }
}
