using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2Cpp;
using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.InteractionSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Shares the haul: a rubble pile picked up in one world is picked up in every world, and the one
    /// unit of quest tally it is worth is paid exactly once per world.
    ///
    /// The mining sync already makes both worlds swing at the same rock, and each world's own authored
    /// code then drops its own pile and takes its own <c>+1</c>. What it deliberately left alone was the
    /// carry, and that is most of the labour: picking a pile up and walking it to the cart is thirty to
    /// sixty seconds, and until this existed both players had to do it for the same rock.
    ///
    /// The pile is NAMED rather than counted. A fungible "somebody dumped one" would only ever replicate
    /// the moment at the cart, leaving the far player's copy lying there for the whole carry - which is
    /// exactly the duplicated work being removed - and "then remove any one of my own piles" would free a
    /// different rock face on each machine, which the mining replay promptly re-fills. The name costs
    /// nothing to invent: the spawning spot already has a cross-machine key, and a spot refuses to be
    /// worked while its pile is still lying there, so the charge count that swing left is a unique second
    /// component.
    ///
    /// The safety rule is one sentence, and it is what makes a floor unnecessary: <b>this world subtracts
    /// at most one unit for a pile, ever, and only for a pile it produced itself.</b> Every pile a world
    /// produces came with exactly one authored addition - the spawn and the addition ride the same
    /// spot-worked event - so the subtractions this can be talked into are bounded above by the additions
    /// already made. The count can therefore never go below where the game alone would have left it,
    /// which matters because the quest gate is an exact equality against zero and nothing in the authored
    /// world ever assigns that variable outright. See <see cref="HaulLedger"/>, where that rule lives
    /// away from every game type and is unit-tested.
    ///
    /// Which variable counts a given rock face's piles is learned, not tabled: during a swing - this
    /// player's own or a teammate's being replayed - the relative write that passes through
    /// <c>GInt.Operate</c> while <see cref="ResourceWork.WorkingSpotKey"/> names a spot is that spot's
    /// tally, and both machines see the same write inside the same window and bind it independently.
    /// </summary>
    internal static class HaulShare
    {
        /// <summary>How many arrived piles one frame acts on, so a burst cannot freeze the world.</summary>
        private const int MaxPerFrame = 32;

        /// <summary>
        /// How long a message waits for the world it talks about to catch up.
        ///
        /// It has to comfortably outlast two things: the authored third of a second the rubble spot
        /// delays its spawn by, and a chunk streaming in. Expiry is silent and safe, because a message
        /// that expires has moved no counter - the count here is already right and only the pile is
        /// still lying about.
        /// </summary>
        private const float HoldSeconds = 20f;

        /// <summary>One pile this world produced and still holds.</summary>
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
        private readonly struct Tally
        {
            internal Tally(string listGuid, string varId, GInt gvar, string name)
            {
                ListGuid = listGuid;
                VarId = varId;
                Var = gvar;
                Name = name;
            }

            internal string ListGuid { get; }

            internal string VarId { get; }

            /// <summary>The variable itself, so the dump is recognised by identity rather than by name.</summary>
            internal GInt Var { get; }

            /// <summary>"List.Variable", for the log.</summary>
            internal string Name { get; }
        }

        private static readonly HaulLedger _ledger = new();

        private static readonly Dictionary<string, Pile> _pileById = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, Pile> _pileByRoot = new();
        private static readonly Dictionary<string, Tally> _tallyBySpot = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Interact_Bhvr_Spot_Rubble> _spawnerBySpot = new(StringComparer.Ordinal);
        private static readonly List<(HaulPileMessage Message, float Deadline)> _held = new();
        private static readonly List<Interact_Bhvr_Spot_Rubble> _repairSpots = new();
        private static readonly Complaints _complaints = new();

        private static Pile? _carried;

        [ThreadStatic]
        private static int _slotDisagreed;

        private static int _lootHits;
        private static int _relativeOps;
        private static int _tagged;
        private static int _untagged;
        private static int _takenSent;
        private static int _dumpedSent;
        private static int _pilesRemoved;
        private static int _dumpedApplied;
        private static int _refusedUntagged;
        private static int _dedupedLocal;
        private static int _dedupedRemote;
        private static int _subRefusedByGame;
        private static int _gaveUp;
        private static int _spotsRepaired;
        private static int _repaid;
        private static uint _sequence;

        /// <summary>How many of the hooks are in place, of four. For <c>coop_status</c>.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How often the pile spawner ran here. Zero after real mining means an inlined hook.</summary>
        internal static int SpawnsSeen { get; private set; }

        /// <summary>
        /// Whether hauling crosses at all - the kill switch, so a session in which this misbehaves can be
        /// finished rather than restarted.
        ///
        /// Safe to throw at any instant, in either direction, and nothing carries state across it: a
        /// pickup not reported is a pile the far world simply keeps and hauls itself, and a dump not
        /// applied is a unit the far world keeps and pays itself. Piles missed while it was off are
        /// refused later by the ordinary never-produced-that-pile rule, which is the safe direction.
        /// </summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>
        /// Forgets this session's piles.
        /// </summary>
        /// <param name="keepBindings">
        /// Whether to keep which variable counts which spot. True after a savegame load, because that is
        /// a fact about how the world was authored rather than about this session, and re-learning it
        /// costs a swing at every spot.
        /// </param>
        internal static void Reset(bool keepBindings = false)
        {
            _ledger.Clear();
            _pileById.Clear();
            _pileByRoot.Clear();
            _spawnerBySpot.Clear();
            _held.Clear();
            _repairSpots.Clear();
            _complaints.Clear();

            if (!keepBindings) _tallyBySpot.Clear();

            _carried = null;

            SpawnsSeen = 0;
            _lootHits = 0;
            _relativeOps = 0;
            _tagged = 0;
            _untagged = 0;
            _takenSent = 0;
            _dumpedSent = 0;
            _pilesRemoved = 0;
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

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;
            int wanted = 0;

            // The pile appearing. Patched on the spawner rather than on the event that leads to it,
            // because the spawner is the only place that knows which clone belongs to which swing - and
            // it survived the build as a real function with both its call sites intact.
            if (HookSwitches.IsOn(HookSwitches.HaulSpawn))
            {
                wanted++;

                if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_Spot_Rubble),
                    nameof(Interact_Bhvr_Spot_Rubble.SpawnRubble),
                    typeof(HaulShare), nameof(SpawnPostfix))) applied++;
            }

            // The pickup. The public funnel both of the game's own call sites go through, so no delegate
            // has to be kept alive on the interop boundary to hear about it.
            if (HookSwitches.IsOn(HookSwitches.HaulLoot))
            {
                wanted++;

                if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_LootAll),
                    nameof(Interact_Bhvr_LootAll.LootAll),
                    typeof(HaulShare), nameof(LootPostfix))) applied++;
            }

            // The dump, and the tally discovery, through one hook: the cart's subtraction is an ordinary
            // relative write, and so is a swing's addition. Which of the two it is depends only on what
            // this machine is doing at the time.
            if (HookSwitches.IsOn(HookSwitches.HaulOperate))
            {
                wanted++;

                if (Hooking.TryPrefix(harmony, typeof(GInt), nameof(GInt.Operate),
                    typeof(HaulShare), nameof(OperatePrefix))) applied++;
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

        /// <summary>
        /// Applies what has arrived and puts right what the last frame's removals left behind. Call once
        /// per frame from a loaded world, after the mining sync and after the variables.
        /// </summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive)
            {
                if (_tagged != 0 || _held.Count != 0 || _pileById.Count != 0) Reset();
                return;
            }

            RepairSpots();

            int budget = MaxPerFrame;
            float now = UnityEngine.Time.unscaledTime;

            while (budget-- > 0 && CoopSession.TryTakeHaulPile(out HaulPileMessage message))
            {
                if (!message.IsWellFormed())
                {
                    _refusedUntagged++;
                    continue;
                }

                if (!Enabled) continue;

                _held.Add((message, now + HoldSeconds));
            }

            Drain(now);
        }

        /// <summary>
        /// Puts back the piles a teammate took out of this world and never dumped.
        ///
        /// The one way this design can leave a world above zero with nothing left to haul, and it is
        /// bounded at one per teammate because a player can carry only one load. Called when a peer
        /// leaves, and by hand from <c>coop_haul repair</c>.
        /// </summary>
        /// <param name="player">Only that teammate's piles, or everybody's when null.</param>
        /// <returns>How many were put back.</returns>
        internal static int RepayOrphans(byte? player = null)
        {
            int repaid = 0;

            try
            {
                List<string> orphans = new(_ledger.Orphans(player));

                foreach (string id in orphans)
                {
                    int hash = id.LastIndexOf('#');
                    if (hash <= 0) continue;

                    string spotKey = id[..hash];
                    if (!byte.TryParse(id[(hash + 1)..], out byte slot)) continue;

                    if (!_spawnerBySpot.TryGetValue(spotKey, out Interact_Bhvr_Spot_Rubble spawner)) continue;
                    if (spawner == null) continue;

                    // The game's own spawner, so the pile arrives with its destroy callback, its flag
                    // and its saved state exactly as a swing's would - and our own postfix names it
                    // again, so hauling it lands this world's unit properly.
                    spawner.SpawnRubble(slot);

                    _ledger.ClearTakenByPeer(id);
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

        /// <summary>
        /// Sends one deliberately unowned pile, so that a live session can be checked in five seconds
        /// without anybody walking to the mine.
        ///
        /// It can never move a counter: the far machine has certainly never produced a pile at this key,
        /// so the path it takes is the refusal - which is the very rule the whole safety argument rests
        /// on, and the one worth seeing fire.
        /// </summary>
        internal static void Ping()
        {
            const string Nowhere = "00000000-0000-4000-8000-000000000000";

            CoopSession.ReportHaulPile(new HaulPileMessage
            {
                SpotKey = Nowhere,
                Slot = 0,
                Kind = HaulPileMessage.KindTaken,
                ListGuid = string.Empty,
                VarId = string.Empty,
                Player = CoopSession.LocalPlayerId.Value,
                Sequence = ++_sequence,
            });

            MelonLogger.Msg($"[Haul] ping: told the session about a pile at '{Complaints.Short(Nowhere)}'/0 that " +
                "no spot owns. Every teammate should refuse it and say so.");
        }

        /// <summary>
        /// What this machine's half of the haul is doing, for <c>coop_status</c>.
        /// </summary>
        internal static IEnumerable<string> Describe()
        {
            yield return $"[Haul] hooks {HooksInstalled}/3 (spawns seen {SpawnsSeen}, pickups seen " +
                $"{_lootHits}, relative writes seen {_relativeOps}), {_tallyBySpot.Count} tally " +
                $"binding(s), {_slotDisagreed} pile(s) named against a disagreeing charge count." +
                (Enabled ? "" : " HAULING IS OFF (coop_haul off).");

            yield return $"[Haul] {_tagged} pile(s) named here, {_untagged} left as this world's own; " +
                $"{_takenSent} pickup(s) and {_dumpedSent} dump(s) told to the session.";

            yield return $"[Haul] piles: {_pilesRemoved} removed to match a teammate, {_held.Count} " +
                $"waiting for their world, {_gaveUp} given up on, {_spotsRepaired} spot(s) put right, " +
                $"{_repaid} put back.";

            yield return $"[Haul] tally: {_dumpedApplied} unit(s) paid for teammates, {_refusedUntagged} " +
                $"refused (this world never had that pile), {_dedupedLocal} skipped here, " +
                $"{_dedupedRemote} already paid, {_subRefusedByGame} refused by the game.";

            string carrying = _carried == null
                ? "carrying nothing"
                : $"carrying '{Complaints.Short(_carried.SpotKey)}'/{_carried.Slot}";

            yield return $"[Haul] {carrying}; {_pileById.Count} named pile(s) still lying here, " +
                $"{_ledger.OrphanCount()} in a teammate's hands (coop_haul repair puts those back).";

            // The line that matters when somebody reports a stuck day: a value above zero with no named
            // pile still lying here is the shape of an unfinishable task, and it names itself. Capped,
            // because a long session binds a tally at every rock face the party has touched.
            int said = 0;

            foreach (KeyValuePair<string, Tally> pair in _tallyBySpot)
            {
                GInt gvar = pair.Value.Var;
                if (gvar == null) continue;

                if (said++ >= 8)
                {
                    yield return $"[Haul] ... and {_tallyBySpot.Count - 8} more tally binding(s).";
                    break;
                }

                yield return $"[Haul] {pair.Value.Name} = {gvar.GetValue()}.";
            }
        }

        private static void Drain(float now)
        {
            for (int index = _held.Count - 1; index >= 0; index--)
            {
                (HaulPileMessage message, float deadline) = _held[index];

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
                    _held.RemoveAt(index);
                    continue;
                }

                if (now < deadline) continue;

                _held.RemoveAt(index);

                string id = message.Id();

                if (!_ledger.IsTagged(id))
                {
                    // The safety rule, and by far the commonest reason to be here: this world never
                    // produced that pile, so it owes nothing for it and has nothing to remove.
                    _refusedUntagged++;

                    if (_complaints.MayComplainAbout(id))
                    {
                        MelonLogger.Msg($"[Haul] player {message.Player} hauled a pile at " +
                            $"'{Complaints.Short(message.SpotKey)}'/{message.Slot} that this world never produced; " +
                            "nothing was removed and nothing was subtracted.");
                    }

                    continue;
                }

                _gaveUp++;

                bool owed = message.Kind == HaulPileMessage.KindDumped && !_ledger.HasSubbed(id);

                MelonLogger.Warning($"[Haul] gave up on the pile at '{Complaints.Short(message.SpotKey)}'/" +
                    $"{message.Slot} after {HoldSeconds:0}s. " + (owed
                        ? "The subtraction never landed, so this world still holds that unit."
                        : "The count here is already right; only the pile is still lying about."));
            }
        }

        /// <summary>
        /// Acts on one arrived message.
        /// </summary>
        /// <returns>False when it should be held and tried again - the pile's own world is behind.</returns>
        private static bool TryApply(in HaulPileMessage message)
        {
            if (!Enabled) return true;

            string id = message.Id();

            // Never produced here - but not yet refused. The rubble spot delays its spawn by a third of
            // a second, and a chunk can take longer than that, so "I have not got that pile" and "I have
            // not got that pile YET" look identical at this instant. The window tells them apart.
            if (!_ledger.IsTagged(id)) return false;

            // A dump implies a pickup, so the removal runs whichever of the two arrived.
            bool removed = TryRemoveHere(id, message.Player);

            if (message.Kind == HaulPileMessage.KindTaken)
            {
                if (!removed) return false;

                _ledger.RecordTakenByPeer(message.Player, id);
                return true;
            }

            // The unit does not wait on the pile. They are independent facts, and a pile that cannot be
            // removed right now - somebody here is mid-pickup - must not also cost this world the
            // subtraction, which is the half of it that a quest gate can see.
            if (!TryPayHere(message, id)) return false;

            return removed;
        }

        private static bool TryRemoveHere(string id, byte player)
        {
            if (_ledger.DecideRemoval(id) != HaulLedger.Decision.Remove) return true;

            if (!_pileById.TryGetValue(id, out Pile pile) || pile.Root == null)
            {
                // Gone already, most likely into this player's own hands. Nothing to do but write it
                // down, so a later message about the same pile does not go looking again.
                Forget(pile);
                _ledger.RecordRemoval(id);
                return true;
            }

            // Somebody here is in the middle of picking it up. Destroying the root inside that tenth of
            // a second means they get no load at all - so wait, which the window makes free.
            if (IsBeingPickedUp(pile)) return false;

            using (RemoteApply<HaulScope>.Enter())
            {
                UnityEngine.Object.Destroy(pile.Root);
            }

            Forget(pile);
            _ledger.RecordRemoval(id);
            _pilesRemoved++;

            if (pile.Owner != null) _repairSpots.Add(pile.Owner);

            MelonLogger.Msg($"[Haul] player {player} took the pile at '{Complaints.Short(pile.SpotKey)}'/" +
                $"{pile.Slot}; removed it here.");

            return true;
        }

        private static bool TryPayHere(in HaulPileMessage message, string id)
        {
            HaulLedger.Decision decision = _ledger.DecideSub(id);

            if (decision == HaulLedger.Decision.DedupSub)
            {
                _dedupedRemote++;
                _ledger.ClearTakenByPeer(id);
                return true;
            }

            if (decision != HaulLedger.Decision.Sub) return true;

            if (!TryResolve(message.ListGuid, message.VarId, out GInt gvar, out string name)) return false;

            // The game refuses a write outright while the active scene is not loaded, silently and with
            // no return value. Recording a subtraction that was discarded would leave this world one
            // unit above zero for good.
            if (!GVarEvents.IsWriteAccepted()) return false;

            int before = gvar.GetValue();

            // Both scopes, and both are load-bearing. The haul scope keeps the tally discovery from
            // enrolling the consequences of the zero crossing as derived on this machine only; the
            // variable scope keeps the assertion watch from telling the peer this world's new total as
            // though it were news of its own.
            using (RemoteApply<HaulScope>.Enter())
            using (RemoteApply<GVarScope>.Enter())
            {
                gvar.Operate(GInt.Operator.Sub, 1, GVarContext.Empty);
            }

            int now = gvar.GetValue();

            if (now == before)
            {
                _subRefusedByGame++;

                if (_complaints.MayComplainAbout(id))
                {
                    MelonLogger.Msg($"[Haul] this world refused the -1 for '{Complaints.Short(message.SpotKey)}'/" +
                        $"{message.Slot}; the game was not accepting writes. Retrying.");
                }

                return false;
            }

            _ledger.RecordSub(id);
            _ledger.ClearTakenByPeer(id);
            _dumpedApplied++;

            MelonLogger.Msg($"[Haul] player {message.Player} dumped the pile at " +
                $"'{Complaints.Short(message.SpotKey)}'/{message.Slot}: '{name}' {before} -> {now} here.");

            return true;
        }

        /// <summary>
        /// Puts right the "a pile lies here" flag of every spot a removal touched.
        ///
        /// A frame late on purpose: Unity defers a destroy to the end of the frame, so the game's own
        /// listener has not run yet when the removal happens. That listener clears the flag after
        /// dropping ONE entry however many are left - the flag is a single bool over a list - and a spot
        /// that records "no pile" while one is still lying there loses the survivor on the next load
        /// while the tally keeps counting it.
        /// </summary>
        private static void RepairSpots()
        {
            if (_repairSpots.Count == 0) return;

            for (int index = 0; index < _repairSpots.Count; index++)
            {
                try
                {
                    Interact_Bhvr_Spot_Rubble spot = _repairSpots[index];
                    if (spot == null) continue;

                    Il2CppSystem.Collections.Generic.List<DestroyCallback> callbacks = spot._destroyCallbacks;
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

            _repairSpots.Clear();
        }

        private static void SpawnPostfix(Interact_Bhvr_Spot_Rubble __instance, int spotsLeft)
        {
            try
            {
                SpawnsSeen++;

                if (!CoopSession.IsLive) return;

                Interact_Bhvr_ResourceSpot owner = __instance._spot;
                string? key = ResourceWork.TryGetKey(owner);

                if (key == null)
                {
                    _untagged++;
                    return;
                }

                _spawnerBySpot[key] = __instance;

                // No tally bound means nobody has yet seen this spot move a counter - a rock face whose
                // piles nothing counts, or one this session has not swung at. Its piles are local
                // labour, and saying so is better than inventing a name a teammate cannot check.
                if (!_tallyBySpot.ContainsKey(key))
                {
                    _untagged++;
                    return;
                }

                Il2CppSystem.Collections.Generic.List<DestroyCallback> callbacks = __instance._destroyCallbacks;

                if (callbacks == null || callbacks.Count == 0)
                {
                    // The spawner declined: the last charge of a spot that drops nothing at the end.
                    _untagged++;
                    return;
                }

                DestroyCallback callback = callbacks[callbacks.Count - 1];
                UnityEngine.GameObject root = callback == null ? null! : callback.gameObject;

                if (root == null)
                {
                    _untagged++;
                    return;
                }

                // The spot's own charge count names the pile, never the argument. A swing passes what it
                // left behind and a savegame restore passes a literal 1, so the argument means two
                // different things and only the spot knows which. Asking the spot needs no patch on the
                // load path, and a patch there is fatal: OnLoadTrigger is overloaded on this class and
                // its bool half calls SpawnRubble, so patching it detours a method that the save system
                // invokes and that reenters another patch of ours. The game aborts with no managed
                // exception to log.
                byte slot = (byte)Math.Clamp(owner == null ? spotsLeft : owner.Charges, 0, byte.MaxValue - 1);

                if (owner != null && owner.Charges != spotsLeft)
                {
                    // Measured rather than assumed, because the whole naming scheme rests on the two
                    // agreeing during a swing. A teammate cannot recognize a pile this world named from
                    // a different number than theirs.
                    _slotDisagreed++;

                    if (_slotDisagreed <= 3)
                    {
                        MelonLogger.Warning($"[Haul] spot '{Complaints.Short(key)}' spawned a pile with {spotsLeft} " +
                            $"left while the spot holds {owner.Charges} charges. The pile was named " +
                            $"{slot}. If pickups stop crossing, this is why.");
                    }
                }

                string id = HaulPileMessage.MakeId(key, slot);

                if (_pileById.ContainsKey(id))
                {
                    // Two piles claiming one name. Rather than share a name that is not unique, this one
                    // stays local: a wrong removal over there is worse than a pile hauled twice here.
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

                _pileById[id] = pile;
                _pileByRoot[pile.RootId] = pile;
                _ledger.Tag(id);
                _tagged++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Haul] naming a spawned pile failed: " + e);
            }
        }

        private static void LootPostfix(Interact_Bhvr_LootAll __instance, Actor actor)
        {
            try
            {
                _lootHits++;

                if (!CoopSession.IsLive) return;
                if (!Enabled) return;

                // Our own removal never loots anything, but say so rather than rely on it.
                if (RemoteApply<HaulScope>.InProgress) return;
                if (!RemotePlayers.IsLocalPlayer(actor)) return;

                UnityEngine.Transform root = __instance.transform == null ? null! : __instance.transform.root;
                if (root == null) return;

                if (!_pileByRoot.TryGetValue(root.gameObject.GetInstanceID(), out Pile pile)) return;

                Forget(pile);
                _ledger.RecordRemoval(pile.Id);

                _carried = pile;
                _takenSent++;

                CoopSession.ReportHaulPile(new HaulPileMessage
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
        /// Sees two different things through one hook, told apart only by what this machine is doing.
        ///
        /// Inside a swing - this player's own or a teammate's being replayed - a relative write is the
        /// spot's tally announcing itself, and it is written down. Outside every window, while this
        /// player is carrying a named pile, a subtraction of one from that pile's own tally variable is
        /// the dump at the cart. That second test is an identity comparison against the variable this
        /// world bound for that spot, which is what lets it recognise the dump without patching the
        /// cart, and what keeps every unrelated counter out of it.
        /// </summary>
        /// <returns>False only to skip a second subtraction a teammate has already paid for this world.</returns>
        private static bool OperatePrefix(GInt __instance, GInt.Operator op, int value)
        {
            try
            {
                if (op == GInt.Operator.Set) return true;

                _relativeOps++;

                if (!CoopSession.IsLive) return true;
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

                if (!_tallyBySpot.TryGetValue(carried.SpotKey, out Tally tally)) return true;
                if (tally.Var == null || tally.Var.Pointer != __instance.Pointer) return true;

                if (op != GInt.Operator.Sub || value != 1)
                {
                    // The authored dump is one pile, one unit. Anything else is not a shape this can
                    // share honestly, so it is left entirely alone and named in the log.
                    if (_complaints.MayComplainAbout(carried.Id))
                    {
                        MelonLogger.Warning($"[Haul] '{tally.Name}' moved by {op} {value} while a pile " +
                            "was being carried; that is not one pile one unit, so it was left alone " +
                            "and not shared.");
                    }

                    return true;
                }

                _carried = null;

                int before = __instance.GetValue();

                if (_ledger.HasSubbed(carried.Id))
                {
                    _dedupedLocal++;

                    MelonLogger.Msg($"[Haul] dumped the pile at '{Complaints.Short(carried.SpotKey)}'/" +
                        $"{carried.Slot}, which a teammate had already dumped for this world; the " +
                        $"authored -1 was skipped so '{tally.Name}' stays at {before}. One pile, one unit.");

                    // The only thing this ever suppresses, and it can only ever be a provable SECOND
                    // unit for one pile. The load itself is still taken by the cart's own conditions, so
                    // the player sees an ordinary dump.
                    return false;
                }

                _ledger.RecordSub(carried.Id);
                _dumpedSent++;

                CoopSession.ReportHaulPile(new HaulPileMessage
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
            if (key == null || _tallyBySpot.ContainsKey(key)) return;

            GVarList parent = gvar.GetParent();
            if (parent == null) return;

            string listGuid = parent.Guid;
            string varId = gvar.Id;
            if (string.IsNullOrEmpty(listGuid) || string.IsNullOrEmpty(varId)) return;

            string name = parent.name + "." + gvar.name;
            _tallyBySpot[key] = new Tally(listGuid, varId, gvar, name);

            MelonLogger.Msg($"[Haul] '{name}' is the tally of the piles spot '{Complaints.Short(key)}' drops; " +
                "hauling one of them now crosses.");
        }

        private static bool TryResolve(string listGuid, string varId, out GInt gvar, out string name)
        {
            gvar = null!;
            name = listGuid + "." + varId;

            SubDatabase_GVars database = ProviderAccess.GVarDatabase;
            if (database == null) return false;

            GVarList list = database.GetGVarListByGuid(listGuid ?? string.Empty);
            if (list == null) return false;

            AGVarBase found = list.GetGVarById(varId ?? string.Empty);
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

                Interaction interaction = pile.Root.GetComponentInChildren<Interaction>(true);
                if (interaction == null) return false;

                // The actor, not IsRunning. That property is `_state != End` and the enum starts at
                // Start, so a pile nobody has ever touched reports itself as running and no removal
                // ever gets past this. An interaction that is really under way has an actor running it,
                // and the game clears that when it ends.
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

            _pileById.Remove(pile.Id);
            _pileByRoot.Remove(pile.RootId);
        }
    }
}
