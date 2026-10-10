using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Diagnostics;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Loot.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2Cpp;
using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.InteractionSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.Loot
{
    /// <summary>
    /// A rubble pile picked up in one world is picked up in every world, and its quest tally unit is paid once per world.
    /// A world subtracts at most one unit per pile, and only for a pile it produced itself (see <see cref="HaulLedger"/>).
    /// </summary>
    internal sealed class HaulShare : ICoopSystem
    {
        private const int MaxPerFrame = 32;
        private const int MaxAwaiting = 256;

        // Outlasts the rubble spot's authored 1/3 s spawn delay and chunk streaming. Expiry moves no counter.
        private const float HoldSeconds = 20f;

        private static readonly Queue<HaulPileMessage> IncomingHaulPiles = new();
        private static readonly HaulLedger Ledger = new();
        private static readonly Dictionary<string, Pile> PileById = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, Pile> PileByRoot = new();
        private static readonly Dictionary<string, Tally> TallyBySpot = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Interact_Bhvr_Spot_Rubble> SpawnerBySpot = new(StringComparer.Ordinal);
        private static readonly List<(HaulPileMessage Message, float Deadline)> Held = new();

        // A teammate's pickup or dump of a pile not spawned here yet. A parked swing can spawn it long after the hold expires.
        private static readonly Dictionary<string, HaulPileMessage> AwaitingPile = new();
        private static readonly List<Interact_Bhvr_Spot_Rubble> SpotsToRepair = new();
        private static readonly Complaints ComplaintLog = new();

        private static Pile? _carried;
        private static int _lootingRoot;

        private static int _slotDisagreed;

        private static int _lootHits;
        private static int _relativeOps;
        private static int _tagged;
        private static int _untagged;
        private static int _takenSent;
        private static int _dumpedSent;
        private static int _pilesRemoved;
        private static int _lateSettled;
        private static int _dumpedApplied;
        private static int _refusedUntagged;
        private static int _dedupedLocal;
        private static int _dedupedRemote;
        private static int _subRefusedByGame;
        private static int _gaveUp;
        private static int _spotsRepaired;
        private static int _repaid;
        private static uint _sequence;

        string? ICoopSystem.HookSwitch => HookSwitches.Haul;

        /// <summary>Piles removed to match a teammate. For tests.</summary>
        internal static int PilesRemoved => _pilesRemoved;

        /// <summary>Piles settled after arriving late. For tests.</summary>
        internal static int LateSettled => _lateSettled;

        /// <summary>How many of the hooks are in place. For <c>coop_status</c>.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How often the pile spawner ran here. Zero after real mining means an inlined hook.</summary>
        internal static int SpawnsSeen { get; private set; }

        /// <summary>Kill switch for hauling. Safe to flip at any time.</summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>Forgets this session's piles.</summary>
        /// <param name="keepBindings">Keep the spot-to-variable bindings, which are authored facts (true after a load).</param>
        private static void Reset(bool keepBindings = false)
        {
            Ledger.Clear();
            PileById.Clear();
            PileByRoot.Clear();
            SpawnerBySpot.Clear();
            Held.Clear();
            AwaitingPile.Clear();
            SpotsToRepair.Clear();
            ComplaintLog.Clear();

            if (!keepBindings) TallyBySpot.Clear();

            _carried = null;

            SpawnsSeen = 0;
            _lootHits = 0;
            _relativeOps = 0;
            _tagged = 0;
            _untagged = 0;
            _takenSent = 0;
            _dumpedSent = 0;
            _pilesRemoved = 0;
            _lateSettled = 0;
            _dumpedApplied = 0;
            _refusedUntagged = 0;
            _dedupedLocal = 0;
            _dedupedRemote = 0;
            _subRefusedByGame = 0;
            _gaveUp = 0;
            _spotsRepaired = 0;
            _repaid = 0;
            _slotDisagreed = 0;
            _sequence = 0;
        }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;
            int wanted = 0;

            // The spawner is the only place that knows which clone belongs to which swing.
            if (HookSwitches.IsOn(HookSwitches.HaulSpawn))
            {
                wanted++;

                if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_Spot_Rubble),
                    nameof(Interact_Bhvr_Spot_Rubble.SpawnRubble),
                    typeof(HaulSharePatches), nameof(HaulSharePatches.SpawnPostfix))) applied++;
            }

            // LootAll is the funnel both of the game's pickup call sites go through.
            if (HookSwitches.IsOn(HookSwitches.HaulLoot))
            {
                wanted++;

                // The pile is read in a prefix because by the postfix the game may have destroyed it.
                Hooking.TryPrefix(harmony, typeof(Interact_Bhvr_LootAll), nameof(Interact_Bhvr_LootAll.LootAll),
                    typeof(HaulSharePatches), nameof(HaulSharePatches.LootPrefix));

                if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_LootAll),
                    nameof(Interact_Bhvr_LootAll.LootAll),
                    typeof(HaulSharePatches), nameof(HaulSharePatches.LootPostfix))) applied++;
            }

            // The cart's dump and a swing's tally addition are both relative writes, so one hook sees both.
            if (HookSwitches.IsOn(HookSwitches.HaulOperate))
            {
                wanted++;

                if (Hooking.TryPrefix(harmony, typeof(GInt), nameof(GInt.Operate),
                    typeof(HaulSharePatches), nameof(HaulSharePatches.OperatePrefix))) applied++;
            }

            HooksInstalled = applied;

            if (applied == wanted && wanted == 3)
            {
                MelonLogger.Msg("[Haul] 3 of 3 hooks installed; hauling rubble is shared - one pile " +
                    "leaves every world exactly once.");
            }
            else
            {
                MelonLogger.Warning($"[Haul] only {applied} of {wanted} hooks installed. A pile a teammate " +
                    "hauls may stay lying in this world. coop_status shows how often each one fired; a " +
                    "zero after real mining means the game inlined it away.");
            }
        }

        /// <summary>Applies arrived messages, after the mining sync and the variables.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
            {
                if (_tagged != 0 || Held.Count != 0 || PileById.Count != 0) Reset();
                return;
            }

            RepairSpots();

            int budget = MaxPerFrame;
            float now = UnityEngine.Time.unscaledTime;

            while (budget-- > 0 && TryTakeHaulPile(out HaulPileMessage message))
            {
                if (!message.IsWellFormed())
                {
                    _refusedUntagged++;
                    continue;
                }

                if (!Enabled) continue;

                Held.Add((message, now + HoldSeconds));
            }

            Drain(now);
        }

        /// <summary>Puts back piles a teammate carried off and never dumped (at most one per teammate).</summary>
        /// <param name="player">Only that teammate's piles, or everybody's when null.</param>
        /// <returns>How many were put back.</returns>
        internal static int RepayOrphans(byte? player = null)
        {
            int repaid = 0;

            try
            {
                List<string> orphans = new(Ledger.Orphans(player));

                foreach (string id in orphans)
                {
                    int hash = id.LastIndexOf('#');
                    if (hash <= 0) continue;

                    string spotKey = id[..hash];
                    if (!byte.TryParse(id[(hash + 1)..], out byte slot)) continue;

                    if (!SpawnerBySpot.TryGetValue(spotKey, out Interact_Bhvr_Spot_Rubble spawner)) continue;
                    if (spawner == null) continue;

                    // The game's own spawner, so the pile gets its destroy callback and saved state, and our postfix names it.
                    spawner.SpawnRubble(slot);

                    Ledger.ClearTakenByPeer(id);
                    repaid++;

                    MelonLogger.Msg($"[Haul] put back the pile at '{Complaints.Short(spotKey)}'/{slot} that a " +
                        "teammate carried off and never dumped; this world can haul it now.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Haul] putting a teammate's piles back failed: " + e);
            }

            _repaid += repaid;
            return repaid;
        }

        /// <summary>Sends an unowned pile so a live session can check that teammates refuse it.</summary>
        internal static void Ping()
        {
            const string nowhere = "00000000-0000-4000-8000-000000000000";

            ReportHaulPile(new HaulPileMessage
            {
                SpotKey = nowhere,
                Slot = 0,
                Kind = HaulPileMessage.KindTaken,
                ListGuid = string.Empty,
                VarId = string.Empty,
                Player = CoopSession.LocalPlayerId.Value,
                Sequence = ++_sequence,
            });

            MelonLogger.Msg($"[Haul] ping: told the session about a pile at '{Complaints.Short(nowhere)}'/0 that " +
                "no spot owns. Every teammate should refuse it and say so.");
        }

        /// <summary>Status lines for <c>coop_status</c>.</summary>
        internal static IEnumerable<string> Describe()
        {
            yield return $"[Haul] hooks {HooksInstalled}/3 (spawns seen {SpawnsSeen}, pickups seen " +
                $"{_lootHits}, relative writes seen {_relativeOps}), {TallyBySpot.Count} tally " +
                $"binding(s), {_slotDisagreed} pile(s) named against a disagreeing charge count." +
                (Enabled ? "" : " HAULING IS OFF (coop_haul off).");

            yield return $"[Haul] {_tagged} pile(s) named here, {_untagged} left as this world's own; " +
                $"{_takenSent} pickup(s) and {_dumpedSent} dump(s) told to the session.";

            yield return $"[Haul] piles: {_pilesRemoved} removed to match a teammate, {Held.Count} " +
                $"waiting for their world, {_gaveUp} given up on, {_spotsRepaired} spot(s) put right, " +
                $"{_repaid} put back.";

            yield return $"[Haul] tally: {_dumpedApplied} unit(s) paid for teammates, {_refusedUntagged} " +
                $"refused (this world never had that pile), {_dedupedLocal} skipped here, " +
                $"{_dedupedRemote} already paid, {_subRefusedByGame} refused by the game.";

            string carrying = _carried == null
                ? "carrying nothing"
                : $"carrying '{Complaints.Short(_carried.SpotKey)}'/{_carried.Slot}";

            yield return $"[Haul] {carrying}; {PileById.Count} named pile(s) still lying here, " +
                $"{Ledger.OrphanCount()} in a teammate's hands (coop_haul repair puts those back).";

            // A tally above zero with no named pile lying here is a stuck day. Capped at 8 bindings.
            int said = 0;

            foreach (KeyValuePair<string, Tally> pair in TallyBySpot)
            {
                GInt? gvar = pair.Value.Var;
                if (gvar == null) continue;

                if (said++ >= 8)
                {
                    yield return $"[Haul] ... and {TallyBySpot.Count - 8} more tally binding(s).";
                    break;
                }

                yield return $"[Haul] {pair.Value.Name} = {gvar.GetValue()}.";
            }
        }

        private static void Drain(float now)
        {
            for (int index = Held.Count - 1; index >= 0; index--)
            {
                (HaulPileMessage message, float deadline) = Held[index];

                bool settled;

                try
                {
                    settled = TryApply(message);
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[Haul] applying a teammate's haul failed: " + e);
                    settled = true;
                }

                if (settled)
                {
                    Held.RemoveAt(index);
                    continue;
                }

                if (now < deadline) continue;

                Held.RemoveAt(index);

                string id = message.Id();

                if (!Ledger.IsTagged(id))
                {
                    // Never produced here yet. Kept until the pile spawns, which hands it back to the hold (SpawnPostfix).
                    _refusedUntagged++;
                    if (AwaitingPile.Count < MaxAwaiting) AwaitingPile[id] = message;

                    if (ComplaintLog.MayComplainAbout(id))
                    {
                        MelonLogger.Msg($"[Haul] player {message.Player} hauled a pile at " +
                            $"'{Complaints.Short(message.SpotKey)}'/{message.Slot} that this world never produced; " +
                            "nothing was removed and nothing was subtracted.");
                    }

                    continue;
                }

                _gaveUp++;

                bool owed = message.Kind == HaulPileMessage.KindDumped && !Ledger.HasSubbed(id);

                MelonLogger.Warning($"[Haul] gave up on the pile at '{Complaints.Short(message.SpotKey)}'/" +
                    $"{message.Slot} after {HoldSeconds:0}s. " + (owed
                        ? "The subtraction never landed, so this world still holds that unit."
                        : "The count here is already right; only the pile is still lying about."));
            }
        }

        /// <returns>False to hold the message and retry.</returns>
        private static bool TryApply(in HaulPileMessage message)
        {
            if (!Enabled) return true;

            string id = message.Id();

            // Not produced here yet. The spawn delay and chunk streaming make "not yet" look like "never".
            if (!Ledger.IsTagged(id)) return false;

            // A dump implies a pickup, so the removal runs for either kind.
            bool removed = TryRemoveHere(id, message.Player);

            if (message.Kind == HaulPileMessage.KindTaken)
            {
                if (!removed) return false;

                Ledger.RecordTakenByPeer(message.Player, id);
                return true;
            }

            // The unit does not wait on the pile, since a quest gate sees only the unit.
            if (!TryPayHere(message, id)) return false;

            return removed;
        }

        private static bool TryRemoveHere(string id, byte player)
        {
            if (Ledger.DecideRemoval(id) != HaulLedger.Decision.Remove) return true;

            if (!PileById.TryGetValue(id, out Pile pile) || pile.Root == null)
            {
                // Gone already, most likely into this player's own hands.
                Forget(pile);
                Ledger.RecordRemoval(id);
                return true;
            }

            // Destroying the root mid-pickup would give the local player no load at all.
            if (IsBeingPickedUp(pile)) return false;

            using (RemoteApply<HaulScope>.Enter())
            {
                UnityEngine.Object.Destroy(pile.Root);
            }

            Forget(pile);
            Ledger.RecordRemoval(id);
            _pilesRemoved++;

            if (pile.Owner != null) SpotsToRepair.Add(pile.Owner);

            MelonLogger.Msg($"[Haul] player {player} took the pile at '{Complaints.Short(pile.SpotKey)}'/" +
                $"{pile.Slot}; removed it here.");

            return true;
        }

        private static bool TryPayHere(in HaulPileMessage message, string id)
        {
            HaulLedger.Decision decision = Ledger.DecideSub(id);

            if (decision == HaulLedger.Decision.DedupSub)
            {
                _dedupedRemote++;
                Ledger.ClearTakenByPeer(id);
                return true;
            }

            if (decision != HaulLedger.Decision.Sub) return true;

            if (!TryResolve(message.ListGuid, message.VarId, out GInt? gvar, out string name)) return false;

            // The game silently drops writes while the active scene is not loaded.
            if (!GVarEvents.IsWriteAccepted()) return false;

            int before = gvar.GetValue();

            // HaulScope keeps the tally discovery out, GVarScope keeps the assertion watch from echoing the new total.
            using (RemoteApply<HaulScope>.Enter())
            using (RemoteApply<GVarScope>.Enter())
            {
                gvar.Operate(GInt.Operator.Sub, 1, GVarContext.Empty);
            }

            int now = gvar.GetValue();

            if (now == before)
            {
                _subRefusedByGame++;

                if (ComplaintLog.MayComplainAbout(id))
                {
                    MelonLogger.Msg($"[Haul] this world refused the -1 for '{Complaints.Short(message.SpotKey)}'/" +
                        $"{message.Slot}; the game was not accepting writes. Retrying.");
                }

                return false;
            }

            Ledger.RecordSub(id);
            Ledger.ClearTakenByPeer(id);
            _dumpedApplied++;

            MelonLogger.Msg($"[Haul] player {message.Player} dumped the pile at " +
                $"'{Complaints.Short(message.SpotKey)}'/{message.Slot}: '{name}' {before} -> {now} here.");

            return true;
        }

        /// <summary>
        /// Resyncs each touched spot's single "has rubble" flag with its pile list, a frame after the removal.
        /// The game's destroy listener clears the flag even when piles remain, losing the survivor on the next load.
        /// </summary>
        private static void RepairSpots()
        {
            if (SpotsToRepair.Count == 0) return;

            for (int index = 0; index < SpotsToRepair.Count; index++)
            {
                try
                {
                    Interact_Bhvr_Spot_Rubble spot = SpotsToRepair[index];
                    if (spot == null) continue;

                    Il2CppSystem.Collections.Generic.List<DestroyCallback>? callbacks = spot._destroyCallbacks;
                    bool has = callbacks != null && callbacks.Count > 0;

                    if (spot._hasRubble == has) continue;

                    spot._hasRubble = has;
                    spot.OnBoolStateChangedEvent?.InvokeEvent(has);
                    _spotsRepaired++;
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[Haul] putting a spot's rubble flag right failed: " + e);
                }
            }

            SpotsToRepair.Clear();
        }

        internal static void SpawnPostfix(Interact_Bhvr_Spot_Rubble __instance, int spotsLeft)
        {
            try
            {
                SpawnsSeen++;

                if (!CoopSession.SharesWorld) return;

                Interact_Bhvr_ResourceSpot? owner = __instance._spot;
                string? key = ResourceWork.GetKey(owner);

                if (key == null)
                {
                    _untagged++;
                    return;
                }

                SpawnerBySpot[key] = __instance;

                // Named even without a bound tally: untallied faces still drop piles, and the pickup is what crosses.
                Il2CppSystem.Collections.Generic.List<DestroyCallback>? callbacks = __instance._destroyCallbacks;

                if (callbacks == null || callbacks.Count == 0)
                {
                    // The spawner declined: the last charge of a spot that drops nothing at the end.
                    _untagged++;
                    return;
                }

                DestroyCallback callback = callbacks[callbacks.Count - 1];
                UnityEngine.GameObject? root = callback == null ? null : callback.gameObject;

                if (root == null)
                {
                    _untagged++;
                    return;
                }

                // The spot's charge count names the pile, since a savegame restore passes a literal 1.
                // Never patch OnLoadTrigger to tell them apart: its overload reenters SpawnRubble and aborts the game.
                byte slot = (byte)Math.Clamp(owner == null ? spotsLeft : owner.Charges, 0, byte.MaxValue - 1);

                if (owner != null && owner.Charges != spotsLeft)
                {
                    // The naming scheme relies on these agreeing during a swing.
                    _slotDisagreed++;

                    if (_slotDisagreed <= 3)
                    {
                        MelonLogger.Warning($"[Haul] spot '{Complaints.Short(key)}' spawned a pile with {spotsLeft} " +
                            $"left while the spot holds {owner.Charges} charges. The pile was named " +
                            $"{slot}. If pickups stop crossing, this is why.");
                    }
                }

                string id = HaulPileMessage.MakeId(key, slot);

                // A catch-up steps several charges in one frame, so later delayed spawns use their own count instead.
                if (PileById.ContainsKey(id) && owner != null && spotsLeft > owner.Charges)
                {
                    slot = (byte)Math.Clamp(spotsLeft, 0, byte.MaxValue - 1);
                    id = HaulPileMessage.MakeId(key, slot);
                }

                if (PileById.ContainsKey(id))
                {
                    // A duplicate name stays local: a wrong remote removal is worse than a pile hauled twice.
                    _untagged++;
                    return;
                }

                Pile pile = new()
                {
                    Id = id,
                    SpotKey = key,
                    Slot = slot,
                    Root = root,
                    RootId = root.GetInstanceID(),
                    Owner = __instance,
                };

                PileById[id] = pile;
                PileByRoot[pile.RootId] = pile;
                Ledger.Tag(id);
                _tagged++;

                // A teammate already took this pile while it did not exist here.
                if (AwaitingPile.Remove(id, out HaulPileMessage late))
                {
                    Held.Add((late, UnityEngine.Time.unscaledTime + HoldSeconds));
                    _lateSettled++;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Haul] naming a spawned pile failed: " + e);
            }
        }

        internal static void LootPrefix(Interact_Bhvr_LootAll __instance)
        {
            try
            {
                _lootingRoot = __instance != null && __instance.transform != null ? __instance.transform.root.gameObject.GetInstanceID() : 0;
            }
            catch
            {
                _lootingRoot = 0;
            }
        }

        internal static void LootPostfix(Actor actor)
        {
            try
            {
                _lootHits++;

                if (!CoopSession.SharesWorld) return;
                if (!Enabled) return;

                if (RemoteApply<HaulScope>.InProgress) return;
                if (!RemotePlayers.IsLocalPlayer(actor)) return;

                int root = _lootingRoot;
                _lootingRoot = 0;
                if (root == 0 || !PileByRoot.TryGetValue(root, out Pile pile)) return;

                Forget(pile);
                Ledger.RecordRemoval(pile.Id);

                _carried = pile;
                _takenSent++;

                ReportHaulPile(new HaulPileMessage
                {
                    SpotKey = pile.SpotKey,
                    Slot = pile.Slot,
                    Kind = HaulPileMessage.KindTaken,
                    ListGuid = string.Empty,
                    VarId = string.Empty,
                    Player = CoopSession.LocalPlayerId.Value,
                    Sequence = ++_sequence,
                });

                MelonLogger.Msg($"[Haul] took the pile at '{Complaints.Short(pile.SpotKey)}'/{pile.Slot}; every " +
                    "teammate's copy of it will be cleared.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Haul] reporting a pickup failed: " + e);
            }
        }

        /// <summary>
        /// Inside a swing a relative write binds the spot's tally. Outside one, a -1 on the carried pile's tally is the cart dump.
        /// </summary>
        /// <returns>False only to skip a second subtraction a teammate has already paid for this world.</returns>
        internal static bool OperatePrefix(GInt __instance, GInt.Operator op, int value)
        {
            try
            {
                if (op == GInt.Operator.Set) return true;

                _relativeOps++;

                if (!CoopSession.SharesWorld) return true;
                if (AGVarBase.IsGVarLoading) return true;

                if (GVarDerived.IsInsideReplicatedCause)
                {
                    Bind(__instance);
                    return true;
                }

                if (!Enabled) return true;
                if (RemoteApply<HaulScope>.InProgress || RemoteApply<GVarScope>.InProgress) return true;

                Pile? carried = _carried;
                if (carried == null) return true;

                if (!TallyBySpot.TryGetValue(carried.SpotKey, out Tally tally)) return true;
                if (tally.Var == null || tally.Var.Pointer != __instance.Pointer) return true;

                if (op != GInt.Operator.Sub || value != 1)
                {
                    // The authored dump is one pile, one unit. Anything else is left alone.
                    if (ComplaintLog.MayComplainAbout(carried.Id))
                    {
                        MelonLogger.Warning($"[Haul] '{tally.Name}' moved by {op} {value} while a pile " +
                            "was being carried; that is not one pile one unit, so it was left alone " +
                            "and not shared.");
                    }

                    return true;
                }

                _carried = null;

                int before = __instance.GetValue();

                if (Ledger.HasSubbed(carried.Id))
                {
                    _dedupedLocal++;

                    MelonLogger.Msg($"[Haul] dumped the pile at '{Complaints.Short(carried.SpotKey)}'/" +
                        $"{carried.Slot}, which a teammate had already dumped for this world; the " +
                        $"authored -1 was skipped so '{tally.Name}' stays at {before}. One pile, one unit.");

                    // Only ever a provable second unit for one pile. The cart still takes the load.
                    return false;
                }

                Ledger.RecordSub(carried.Id);
                _dumpedSent++;

                ReportHaulPile(new HaulPileMessage
                {
                    SpotKey = carried.SpotKey,
                    Slot = carried.Slot,
                    Kind = HaulPileMessage.KindDumped,
                    ListGuid = tally.ListGuid,
                    VarId = tally.VarId,
                    Player = CoopSession.LocalPlayerId.Value,
                    Sequence = ++_sequence,
                });

                MelonLogger.Msg($"[Haul] dumped the pile at '{Complaints.Short(carried.SpotKey)}'/{carried.Slot}: " +
                    $"'{tally.Name}' {before} -> {before - 1} here.");

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Haul] watching a relative write failed: " + e);
                return true;
            }
        }

        private static void Bind(GInt gvar)
        {
            string? key = ResourceWork.WorkingSpotKey;
            if (key == null || TallyBySpot.ContainsKey(key)) return;

            GVarList? parent = gvar.GetParent();
            if (parent == null) return;

            string listGuid = parent.Guid;
            string varId = gvar.Id;
            if (string.IsNullOrEmpty(listGuid) || string.IsNullOrEmpty(varId)) return;

            string name = parent.name + "." + gvar.name;
            TallyBySpot[key] = new Tally(listGuid, varId, gvar, name);

            MelonLogger.Msg($"[Haul] '{name}' is the tally of the piles spot '{Complaints.Short(key)}' drops; " +
                "hauling one of them now crosses.");
        }

        private static bool TryResolve(string? listGuid, string? varId, [NotNullWhen(true)] out GInt? gvar, out string name)
        {
            gvar = null;
            name = listGuid + "." + varId;

            SubDatabase_GVars? database = ProviderAccess.GVarDatabase;
            if (database == null) return false;

            GVarList? list = database.GetGVarListByGuid(listGuid ?? string.Empty);
            if (list == null) return false;

            AGVarBase? found = list.GetGVarById(varId ?? string.Empty);
            if (found == null) return false;

            GInt? asInt = found.TryCast<GInt>();
            if (asInt == null) return false;

            gvar = asInt;
            name = list.name + "." + found.name;
            return true;
        }

        private static bool IsBeingPickedUp(Pile pile)
        {
            try
            {
                if (pile.Root == null) return false;

                Interaction? interaction = pile.Root.GetComponentInChildren<Interaction>(true);
                if (interaction == null) return false;

                // Not IsRunning, which reports an untouched pile as running.
                return interaction.RunningActor != null;
            }
            catch
            {
                return false;
            }
        }

        private static void Forget(Pile? pile)
        {
            if (pile == null) return;

            PileById.Remove(pile.Id);
            PileByRoot.Remove(pile.RootId);
        }

        void ICoopSystem.RegisterMessages()
        {
            // Stamped with who actually hauled it, for the same reason resource work is.
            CoopSession.Register<HaulPileMessage>(MessageIds.HaulPile, (peer, m) =>
            {
                if (CoopSession.HostsWorld && CoopSession.TryGetSender(peer, out PlayerId sender)) m = m with { Player = sender.Value };

                CoopSession.RelayAndQueue(peer, m, IncomingHaulPiles, CoopSession.MaxPendingFromHost, "hauled piles");
            });
        }

        void ICoopSystem.EndSession()
        {
            IncomingHaulPiles.Clear();
        }

        /// <summary>Reports that one rubble pile has left this player's world.</summary>
        private static void ReportHaulPile(in HaulPileMessage message)
            => CoopSession.SendIfLive(message);

        /// <summary>Takes the next reported pile, if one is waiting.</summary>
        private static bool TryTakeHaulPile(out HaulPileMessage message)
            => IncomingHaulPiles.TryDequeue(out message);

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            // Rubble piles they carried off and never dumped go back so this world can haul them.
            RepayOrphans(player.Value);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset(keepHaulBindings);

        private sealed class Pile
        {
            internal string Id = string.Empty;
            internal string SpotKey = string.Empty;
            internal byte Slot;
            internal int RootId;
            internal UnityEngine.GameObject Root = null!;
            internal Interact_Bhvr_Spot_Rubble Owner = null!;
        }

        /// <summary>The variable that counts one spot's piles, once this world has seen it move.</summary>
        private readonly struct Tally(string listGuid, string varId, GInt? gvar, string name)
        {
            internal string ListGuid { get; } = listGuid;

            internal string VarId { get; } = varId;

            internal GInt? Var { get; } = gvar;

            /// <summary>"List.Variable", for the log.</summary>
            internal string Name { get; } = name;
        }
    }
}
