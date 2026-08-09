using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.InteractionSystem;
using Il2CppDrova.Saveables;
using Il2CppDrova.Seaveables;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Shares work at resource spots - rock faces, ore veins, herbs, trees - so a swing one player
    /// takes is a swing in every world.
    ///
    /// This is the answer to the mine's mining task not crossing, and it is deliberately not built
    /// the way the rest of this mod's world state is. A worked spot has consequences scattered over
    /// half a dozen authored components: the charge count, the erosion sprite, a spawned rubble pile
    /// carrying its own destroy callback, a quest tally, a node that deactivates itself, particles,
    /// audio. Replicating that STATE means replicating each of them and getting every one right - and
    /// the game's own restore path will not do it, because the rubble's restore spawns a pile
    /// unconditionally (a second pile every time a message arrives) and its false direction does
    /// nothing at all, so "the pile is gone" could never cross. Worse, the charge count is not part
    /// of that state at all: it lives in a separate saver, so a world could be given the eroded
    /// sprite and still have a fully minable rock.
    ///
    /// So what crosses is the WORK. One message per swing, and each machine derives all of it from
    /// its own vanilla code, which is already correct. There is no mod code inside any consequence.
    ///
    /// The wire value is the ABSOLUTE charge count afterwards, and an apply only ever steps its own
    /// spot DOWN to it. Charges never rise except on a savegame load, so that is idempotent and
    /// commutative: redelivery, out-of-order arrival and a message from a machine that is behind are
    /// all no-ops, and two players swinging at one rock converge with no claim and no round trip. The
    /// price is that a genuinely simultaneous swing banks one charge instead of two - self-correcting,
    /// and preferable to a delta that would hand out a free pile on a redelivery.
    ///
    /// What is NOT shared, by design: the hauling. The tally counts piles in THIS world, lying or
    /// carried, and the replayed swing both spawns the pile and moves the tally, so that invariant
    /// holds per machine for free. Relaying a cart dump as well would subtract on a machine that
    /// still has its pile and drive the count below zero, past the gate the quest waits on.
    /// </summary>
    internal static class ResourceWork
    {
        /// <summary>How many messages one frame applies, so a snapshot cannot freeze the world.</summary>
        private const int MaxPerFrame = 32;

        /// <summary>How often one spot may name itself in the log, in seconds.</summary>
        private const float QuietSeconds = 5f;

        private static readonly Dictionary<int, string> _keyBySpot = new();
        private static readonly Dictionary<string, Interact_Bhvr_ResourceSpot> _spotByKey = new(StringComparer.Ordinal);

        /// <summary>
        /// Where a spot that is not here yet must end up.
        ///
        /// A cursor, never a queue, and never dropped: Drova streams chunks continuously, so "that
        /// object does not exist yet" is the ordinary case rather than an error, and a value quietly
        /// discarded is the failure this project has paid for more than any other. Merged by
        /// minimum, so it stays idempotent, and applied when the spot registers itself at stream-in.
        /// </summary>
        private static readonly Dictionary<string, byte> _pending = new(StringComparer.Ordinal);

        private static readonly List<string> _justRegistered = new();
        private static readonly Dictionary<string, byte> _lastSaid = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _lastComplaint = new(StringComparer.Ordinal);

        [ThreadStatic]
        private static int _localWorkDepth;

        private static int _hooksInstalled;
        private static int _minigameHits;
        private static int _registerHits;
        private static int _worked;
        private static int _applied;
        private static int _alreadyThere;
        private static int _deferred;
        private static int _refused;
        private static int _snapshotSent;
        private static int _snapshotReceived;
        private static uint _snapshotAnnounced;
        private static bool _snapshotComplete;
        private static uint _sequence;
        private static bool _enabled = true;

        /// <summary>Whether this machine is inside its own player's swing right now.</summary>
        internal static bool IsLocalWorkInProgress => _localWorkDepth > 0;

        /// <summary>How many of the hooks are in place, of four. For <c>coop_status</c>.</summary>
        internal static int HooksInstalled => _hooksInstalled;

        /// <summary>How many local swings were seen. Zero after a real swing means an inlined hook.</summary>
        internal static int MinigameHits => _minigameHits;

        /// <summary>How many spots made themselves addressable. For <c>coop_status</c>.</summary>
        internal static int RegisterHits => _registerHits;

        /// <summary>How many swings were told to the session.</summary>
        internal static int Worked => _worked;

        /// <summary>How many teammates' swings were replayed here.</summary>
        internal static int Applied => _applied;

        /// <summary>How many arrived at a spot already that far along.</summary>
        internal static int AlreadyThere => _alreadyThere;

        /// <summary>How many are waiting for their chunk to stream in.</summary>
        internal static int Pending => _pending.Count;

        /// <summary>How many were held because the spot was mid-interaction.</summary>
        internal static int Deferred => _deferred;

        /// <summary>How many were refused outright. Should be zero.</summary>
        internal static int Refused => _refused;

        /// <summary>How many spots are addressable here.</summary>
        internal static int Registered => _spotByKey.Count;

        /// <summary>How many spot records the host sent a joining player.</summary>
        internal static int SnapshotSent => _snapshotSent;

        /// <summary>How many arrived from the host on joining.</summary>
        internal static int SnapshotReceived => _snapshotReceived;

        /// <summary>
        /// Whether teammates' swings are replayed here at all - the kill switch, so a session in
        /// which this misbehaves can be finished rather than restarted.
        /// </summary>
        internal static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Forgets what this session knew. Never rolls back applied work: a node a teammate emptied
        /// is real progress in this player's own save and stays theirs.
        /// </summary>
        internal static void Reset()
        {
            _keyBySpot.Clear();
            _spotByKey.Clear();
            _pending.Clear();
            _justRegistered.Clear();
            _lastSaid.Clear();
            _lastComplaint.Clear();

            _minigameHits = 0;
            _registerHits = 0;
            _worked = 0;
            _applied = 0;
            _alreadyThere = 0;
            _deferred = 0;
            _refused = 0;
            _snapshotSent = 0;
            _snapshotReceived = 0;
            _snapshotAnnounced = 0;
            _snapshotComplete = false;
            _sequence = 0;
        }

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            // The swing. Patched on the event listener rather than on the private handler it calls:
            // the listener is the target of a delegate registration, so IL2CPP has to emit a real
            // function for it, whereas a private method with one caller is the classic inlining
            // victim - the trap that once made a whole build's journal sharing silently dead.
            if (Hooking.TryPrefix(harmony, typeof(Interact_Bhvr_ResourceSpot),
                nameof(Interact_Bhvr_ResourceSpot.MinigameFinishedEventListener),
                typeof(ResourceWork), nameof(MinigamePrefix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_ResourceSpot),
                nameof(Interact_Bhvr_ResourceSpot.MinigameFinishedEventListener),
                typeof(ResourceWork), nameof(MinigamePostfix))) applied++;

            // A spot becoming addressable, by two independent paths so that one being inlined away
            // does not take the subsystem with it. LoadCallback is a public override reached
            // virtually from the save system; Start is a Unity message invoked natively.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_ResourceSpot),
                nameof(Saveable_ResourceSpot.LoadCallback),
                typeof(ResourceWork), nameof(RegisterPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_ResourceSpot),
                nameof(Saveable_ResourceSpot.Start),
                typeof(ResourceWork), nameof(RegisterPostfix))) applied++;

            _hooksInstalled = applied;

            if (applied == 4)
            {
                MelonLogger.Msg("[Work] 4 of 4 hooks installed; work at resource spots is shared.");
            }
            else
            {
                MelonLogger.Warning($"[Work] only {applied} of 4 hooks installed. Mining may not " +
                    "cross. coop_status shows how often each one fired - a zero after a real swing " +
                    "means the game inlined it away.");
            }
        }

        /// <summary>
        /// Applies what has arrived, and hands pending cursors to spots that have just streamed in.
        /// Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Reset();
                return;
            }

            DrainRegistrations();

            int budget = MaxPerFrame;

            while (budget-- > 0 && CoopSession.TryTakeResourceWork(out ResourceWorkMessage message))
            {
                ApplyWork(message);
            }

            while (budget-- > 0 && CoopSession.TryTakeResourceSpotSnapshot(out ResourceSpotSnapshotMessage chunk))
            {
                if (chunk.Records == null) continue;

                foreach (ResourceSpotSnapshotMessage.Record record in chunk.Records)
                {
                    _snapshotReceived++;
                    Park(record.RootKey, record.Charges);
                }
            }

            if (_snapshotComplete)
            {
                _snapshotComplete = false;

                MelonLogger.Msg($"[Work] {_snapshotReceived} of {_snapshotAnnounced} announced " +
                    $"resource spot(s) arrived; {_spotByKey.Count} spot(s) are loaded here and the " +
                    "rest are held until their part of the world streams in.");
            }
        }

        /// <summary>
        /// The host has finished sending the spot table.
        /// </summary>
        /// <param name="recordCount">How many records it believes it sent.</param>
        internal static void CompleteSnapshot(uint recordCount)
        {
            _snapshotAnnounced = recordCount;
            _snapshotComplete = true;
        }

        /// <summary>
        /// Sends a joining player how far every resource spot in this world has been worked.
        ///
        /// Read out of the savegame rather than off live objects, and that is the only complete
        /// answer rather than merely the cheap one: a save root exists only once its chunk has
        /// streamed, so a walk of live objects would send exactly the spots around the host's own
        /// player - which is the set the joining player is least likely to be missing.
        /// </summary>
        /// <param name="peer">The joining player.</param>
        internal static void SendTo(INetPeer peer)
        {
            try
            {
                Savegame save = Savegame.Current;
                if (save?.Data == null) return;

                Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> data = save.Data.Dict;
                if (data == null) return;

                string prefix = ASaveRoot.GAMEOBJECT_SAVE_DATA_PREFIX;
                List<ResourceSpotSnapshotMessage.Record> records = new();
                int sent = 0;

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Il2CppSystem.Object> pair in data)
                {
                    string key = pair.Key;
                    if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal)) continue;

                    string instanceKey = key[prefix.Length..];
                    if (!ResourceWorkMessage.IsReplicableKey(instanceKey)) continue;

                    SaveData_GenericDictionary objectData = pair.Value?.TryCast<SaveData_GenericDictionary>();
                    if (objectData == null) continue;

                    // Non-generic on purpose: TryGetData<T> is a generic whose closed form may have no
                    // body in an IL2CPP build.
                    SaveData_Int charges = objectData.GetGenericData("Saveable_ResourceSpot")
                        ?.TryCast<SaveData_Int>();

                    if (charges == null) continue;

                    records.Add(new ResourceSpotSnapshotMessage.Record
                    {
                        RootKey = instanceKey,
                        Charges = (byte)Math.Clamp(charges.Value, 0, byte.MaxValue),
                    });

                    if (records.Count >= ResourceSpotSnapshotMessage.MaxRecords)
                    {
                        NetworkAccess.Send(peer, new ResourceSpotSnapshotMessage { Records = [.. records] });
                        sent += records.Count;
                        records.Clear();
                    }
                }

                if (records.Count > 0)
                {
                    NetworkAccess.Send(peer, new ResourceSpotSnapshotMessage { Records = [.. records] });
                    sent += records.Count;
                }

                NetworkAccess.Send(peer, new ResourceSpotSnapshotEndMessage { RecordCount = (uint)sent });

                _snapshotSent = sent;

                MelonLogger.Msg($"[Work] a joining player was sent {sent} resource spot(s), so a rock " +
                    "this world has already been mined out of stays mined out for them.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] building the resource-spot snapshot failed: " + e);
            }
        }

        private static void MinigamePrefix(Interact_Bhvr_ResourceSpot __instance, out int __state)
        {
            __state = __instance.Charges;
            _localWorkDepth++;
        }

        private static void MinigamePostfix(Interact_Bhvr_ResourceSpot __instance, int __state)
        {
            try
            {
                _minigameHits++;

                // A replay must never be reported back as local news.
                if (RemoteApply<WorkScope>.InProgress) return;
                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;

                int now = __instance.Charges;
                if (now == __state) return;

                if (!_keyBySpot.TryGetValue(__instance.GetInstanceID(), out string key))
                {
                    _refused++;

                    if (MayComplainAbout("spot#" + __instance.GetInstanceID()))
                    {
                        MelonLogger.Warning("[Work] a spot was worked here that has no shareable save " +
                            "root; that swing stays in this world only.");
                    }

                    return;
                }

                byte charges = (byte)Math.Clamp(now, 0, byte.MaxValue);

                if (_lastSaid.TryGetValue(key, out byte said) && said == charges) return;

                _lastSaid[key] = charges;
                _worked++;

                CoopSession.ReportResourceWork(new ResourceWorkMessage
                {
                    RootKey = key,
                    ChargesAfter = charges,
                    ChargesBefore = (byte)Math.Clamp(__state, 0, byte.MaxValue),
                    Flags = ResourceWorkMessage.FlagByPlayer,
                    Player = CoopSession.LocalPlayerId.Value,
                    Sequence = ++_sequence,
                });

                MelonLogger.Msg($"[Work] worked '{Short(key)}' ({Name(__instance)}): {__state} -> {now}.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] reporting a swing failed: " + e);
            }
            finally
            {
                if (_localWorkDepth > 0) _localWorkDepth--;
            }
        }

        /// <summary>
        /// Makes a spot addressable once the game has restored its own charges.
        ///
        /// Registration only; nothing is applied here. The save system holds its loading flag up for
        /// the whole of the load callback, and the spot's own writeback is gated on that flag - so a
        /// value applied inside this callback would land in the world and never reach the save.
        /// The key is queued instead and drained on the next frame with the flag down.
        /// </summary>
        private static void RegisterPostfix(Saveable_ResourceSpot __instance)
        {
            try
            {
                Interact_Bhvr_ResourceSpot spot = __instance._interactBhvr;
                if (spot == null) return;

                ASaveRoot root = __instance.GetComponent<ASaveRoot>();
                if (root == null) root = __instance.GetComponentInParent<ASaveRoot>();
                if (root == null) return;

                // Never ask a dynamic root for its key: producing one MINTS a per-savegame number as
                // a side effect, and those numbers alias between machines - this world's object 2 is
                // an unrelated object 2 over there. An aliased write corrupts something the player
                // was never near, which is far worse than a miss.
                SaveRoot_Dynamic dynamic = root.TryCast<SaveRoot_Dynamic>();
                if (dynamic != null && dynamic.IsDynamic()) return;

                string key = root.GetGameObjectInstanceKey();
                if (!ResourceWorkMessage.IsReplicableKey(key)) return;

                _registerHits++;

                _keyBySpot[spot.GetInstanceID()] = key;
                _spotByKey[key] = spot;

                if (_pending.ContainsKey(key)) _justRegistered.Add(key);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] registering a resource spot failed: " + e);
            }
        }

        /// <summary>
        /// Hands pending cursors to spots that streamed in since the last frame.
        /// </summary>
        private static void DrainRegistrations()
        {
            if (_justRegistered.Count == 0) return;

            for (int index = 0; index < _justRegistered.Count; index++)
            {
                string key = _justRegistered[index];

                if (!_pending.TryGetValue(key, out byte charges)) continue;
                if (!_spotByKey.TryGetValue(key, out Interact_Bhvr_ResourceSpot spot) || spot == null) continue;

                if (ApplySilently(spot, charges))
                {
                    _pending.Remove(key);
                }
            }

            _justRegistered.Clear();
        }

        private static void ApplyWork(in ResourceWorkMessage message)
        {
            try
            {
                if (!ResourceWorkMessage.IsReplicableKey(message.RootKey))
                {
                    _refused++;
                    return;
                }

                if (!_enabled) return;

                if (!_spotByKey.TryGetValue(message.RootKey, out Interact_Bhvr_ResourceSpot spot) || spot == null)
                {
                    Park(message.RootKey, message.ChargesAfter);

                    if (MayComplainAbout(message.RootKey))
                    {
                        MelonLogger.Msg($"[Work] '{Short(message.RootKey)}' is not streamed in here; " +
                            $"held at {message.ChargesAfter} until it loads.");
                    }

                    return;
                }

                // Mid-swing here. Stepping its charges under it would fight the local minigame, so
                // the cursor waits - it is idempotent, so waiting costs nothing.
                if (spot._isRunning)
                {
                    _deferred++;
                    Park(message.RootKey, message.ChargesAfter);
                    return;
                }

                int before = spot.Charges;

                if (before <= message.ChargesAfter)
                {
                    _alreadyThere++;

                    if (before < message.ChargesAfter && MayComplainAbout(message.RootKey))
                    {
                        MelonLogger.Warning($"[Work] '{Short(message.RootKey)}' arrived " +
                            $"{message.ChargesBefore} -> {message.ChargesAfter} but this world held " +
                            $"{before}; kept {before}.");
                    }

                    return;
                }

                Replay(spot, message.ChargesAfter);

                _applied++;

                MelonLogger.Msg($"[Work] player {message.Player} worked '{Short(message.RootKey)}' " +
                    $"({Name(spot)}): {before} -> {spot.Charges}.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] applying a teammate's swing failed: " + e);
            }
        }

        /// <summary>
        /// Replays a teammate's swings as swings, so every consequence runs through this machine's
        /// own authored code: the sprite erodes, the pile falls, the tally moves, the node hides.
        /// </summary>
        private static void Replay(Interact_Bhvr_ResourceSpot spot, byte target)
        {
            using (RemoteApply<WorkScope>.Enter())
            {
                Actor worker = PlayerAccess.TryGetPlayer(out Actor local) ? local : null!;

                // The rubble spot's own listener asks whether ITS actor is a player, and that field is
                // only ever set when somebody begins an interaction with it. Left null, the replay
                // moves the charges and paints nothing at all.
                Interact_Bhvr_Spot_Rubble rubble = spot.GetComponent<Interact_Bhvr_Spot_Rubble>();
                Actor saved = rubble != null ? rubble._actor : null!;

                if (rubble != null) rubble._actor = worker;

                try
                {
                    // One step per charge, through the game's own pair. Deliberately not SetCharges,
                    // which fires an extra not-worked event per step and would double the writeback.
                    while (spot.Charges > target)
                    {
                        spot.AdjustCharges();
                        spot.OnSpotWorkedEvent.InvokeEvent(new WorkArgs(spot.Charges, true, worker));
                    }
                }
                finally
                {
                    if (rubble != null) rubble._actor = saved;
                }
            }
        }

        /// <summary>
        /// Puts a spot where it belongs without acting it out: for the join snapshot and for anything
        /// that arrived before its chunk did.
        ///
        /// A joining player has no business inheriting the host's undumped rubble - their tally
        /// starts at zero with no piles, which is self-consistent - and a cursor that waited out a
        /// chunk load is history rather than an event. Both consumers that must not double return
        /// immediately when the work flag is false, so nothing spawns and no tally moves; the node
        /// that hides itself when empty acts either way, which is what makes an exhausted herb still
        /// look exhausted.
        /// </summary>
        /// <returns>False when the spot could not be moved.</returns>
        private static bool ApplySilently(Interact_Bhvr_ResourceSpot spot, byte target)
        {
            try
            {
                if (spot._isRunning) return false;
                if (spot.Charges <= target) return true;

                using (RemoteApply<WorkScope>.Enter())
                {
                    spot.SetCharges(target, false);

                    // Repaint from a value computed HERE out of the charges just set, never one off
                    // the wire and never a default: a synthesized zero would deactivate an intact
                    // rock face on the spots whose sprite list holds a single entry.
                    Interact_Bhvr_Spot_Rubble rubble = spot.GetComponent<Interact_Bhvr_Spot_Rubble>();
                    if (rubble != null) rubble.OnLoadTrigger(rubble.SaveableState);
                }

                _applied++;
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] settling a resource spot failed: " + e);
                return false;
            }
        }

        private static void Park(string key, byte charges)
        {
            if (!ResourceWorkMessage.IsReplicableKey(key)) return;

            _pending[key] = _pending.TryGetValue(key, out byte held) ? Math.Min(held, charges) : charges;

            if (_spotByKey.ContainsKey(key)) _justRegistered.Add(key);
        }

        private static bool MayComplainAbout(string key)
        {
            float now = UnityEngine.Time.unscaledTime;

            if (_lastComplaint.TryGetValue(key, out float last) && now - last < QuietSeconds) return false;

            _lastComplaint[key] = now;
            return true;
        }

        private static string Short(string key)
        {
            return key.Length <= 8 ? key : key[..8];
        }

        private static string Name(Interact_Bhvr_ResourceSpot spot)
        {
            try
            {
                return spot?.gameObject?.name ?? "?";
            }
            catch
            {
                return "?";
            }
        }
    }
}
