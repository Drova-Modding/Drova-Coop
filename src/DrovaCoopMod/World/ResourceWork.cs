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
    /// The haul is not derived here, and no longer left out either. The pile a swing drops is named by
    /// this file's own spot key together with the charge count the swing left, and
    /// <see cref="HaulShare"/> carries it from there: one pile leaves every world exactly once, and the
    /// one unit of tally that belongs to it moves exactly once with it.
    /// </summary>
    internal static class ResourceWork
    {
        /// <summary>How many messages one frame applies, so a snapshot cannot freeze the world.</summary>
        private const int MaxPerFrame = 32;

        /// <summary>How often the loaded spots are walked while something is waiting for one.</summary>
        private const float LookSeconds = 1f;

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

        private static float _lastLook;

        // Which parked cursors came from a teammate's swing rather than from the join snapshot. The
        // difference decides what happens when the spot finally turns up: a swing is acted out, so
        // this world drops its own pile and moves its own tally, while a snapshot is settled in
        // silence, because a rock mined out before this player arrived owes them nothing.
        private static readonly HashSet<string> _pendingLive = new();
        private static uint _snapshotAnnounced;
        private static bool _snapshotComplete;
        private static uint _sequence;
        private static bool _enabled = true;

        /// <summary>Whether this machine is inside its own player's swing right now.</summary>
        internal static bool IsLocalWorkInProgress => _localWorkDepth > 0;

        /// <summary>How many of the hooks are in place, of four. For <c>coop_status</c>.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How many local swings were seen. Zero after a real swing means an inlined hook.</summary>
        internal static int MinigameHits { get; private set; }

        /// <summary>How many spots made themselves addressable. For <c>coop_status</c>.</summary>
        internal static int RegisterHits { get; private set; }

        /// <summary>How many swings were told to the session.</summary>
        internal static int Worked { get; private set; }

        /// <summary>How many teammates' swings were replayed here.</summary>
        internal static int Applied { get; private set; }

        /// <summary>
        /// How many spots were named by where they stand because nothing else named them.
        ///
        /// Zero in the bandit mine means the fallback is not reaching the spots it exists for, and
        /// nothing there will cross.
        /// </summary>
        internal static int PlacedByPosition { get; private set; }

        /// <summary>
        /// How many of those were named only when somebody asked, rather than when they woke up. For
        /// <c>coop_status</c>: all of them means the Awake patch never fires.
        /// </summary>
        internal static int PlacedLate { get; private set; }

        /// <summary>
        /// How many spots were handed their charge count without acting it out - the join snapshot and
        /// anything that waited out a chunk load.
        ///
        /// Counted apart from <see cref="Applied"/> rather than with it, because the two are opposites
        /// where the haul is concerned: a replay drops a pile and moves the tally, a settle does
        /// neither. "Charges handed over with no pile and no tally" is exactly the quantity a haul
        /// divergence is diagnosed from, and one number covering both cannot say it.
        /// </summary>
        internal static int Settled { get; private set; }

        /// <summary>
        /// The cross-machine name of a spot, or null when it has none.
        /// </summary>
        /// <param name="spot">The spot.</param>
        internal static string? TryGetKey(Interact_Bhvr_ResourceSpot spot)
        {
            if (spot == null) return null;

            if (_keyBySpot.TryGetValue(spot.GetInstanceID(), out string key)) return key;

            // Named here rather than only at Awake, because a name minted from a position needs only
            // the position, and by the time anybody asks for a key the spot is certainly standing
            // where it belongs. The Awake pass stays because a spot named early can also be spoken
            // about early - a teammate's swing arriving before anybody here has touched the rock - but
            // nothing may depend on it having run: the mine's rubble faces reached a swing unnamed,
            // and an unnamed spot silently keeps its work, and its piles, to one world.
            return NameByPosition(spot, late: true);
        }

        /// <summary>
        /// The spot this machine is swinging at or replaying right now, by key, and null otherwise.
        ///
        /// So that a relative write passing through while it is set can be attributed to the spot that
        /// caused it - which is how the haul learns, with no table and no wire traffic, which variable
        /// counts a given rock face's piles.
        /// </summary>
        internal static string? WorkingSpotKey { get; private set; }

        /// <summary>How many arrived at a spot already that far along.</summary>
        internal static int AlreadyThere { get; private set; }

        /// <summary>How many are waiting for their chunk to stream in.</summary>
        internal static int Pending => _pending.Count;

        /// <summary>How many were held because the spot was mid-interaction.</summary>
        internal static int Deferred { get; private set; }

        /// <summary>How many were refused outright. Should be zero.</summary>
        internal static int Refused { get; private set; }

        /// <summary>How many spots are addressable here.</summary>
        internal static int Registered => _spotByKey.Count;

        /// <summary>How many spot records the host sent a joining player.</summary>
        internal static int SnapshotSent { get; private set; }

        /// <summary>How many arrived from the host on joining.</summary>
        internal static int SnapshotReceived { get; private set; }

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
            _pendingLive.Clear();
            _justRegistered.Clear();
            _lastSaid.Clear();
            _lastComplaint.Clear();

            MinigameHits = 0;
            RegisterHits = 0;
            PlacedByPosition = 0;
            PlacedLate = 0;
            _lastLook = 0f;
            Worked = 0;
            Applied = 0;
            Settled = 0;
            AlreadyThere = 0;
            Deferred = 0;
            Refused = 0;
            SnapshotSent = 0;
            SnapshotReceived = 0;
            _snapshotAnnounced = 0;
            _snapshotComplete = false;
            _sequence = 0;

            WorkingSpotKey = null;
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

            // The spots the save system never names. Patched on the behavior's own Awake because there
            // is no saveable component to hang it off - that absence is the whole case. A Unity message
            // is invoked from native code, so it survives as a real function.
            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_ResourceSpot),
                "Awake", typeof(ResourceWork), nameof(AwakePostfix))) applied++;

            HooksInstalled = applied;

            if (applied == 5)
            {
                MelonLogger.Msg("[Work] 5 of 5 hooks installed; work at resource spots is shared.");
            }
            else
            {
                MelonLogger.Warning($"[Work] only {applied} of 5 hooks installed. Mining may not " +
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
            if (!CoopSession.IsLive)
            {
                Reset();
                return;
            }

            LookForPending();
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
                    SnapshotReceived++;
                    Park(record.RootKey, record.Charges, live: false);
                }
            }

            if (_snapshotComplete)
            {
                _snapshotComplete = false;

                MelonLogger.Msg($"[Work] {SnapshotReceived} of {_snapshotAnnounced} announced " +
                    $"resource spot(s) arrived; {_spotByKey.Count} spot(s) are loaded here and the " +
                    "rest are held until their part of the world streams in.");
            }
        }

        /// <summary>
        /// Goes looking for the spots that pending position keys name.
        ///
        /// A spot the save system never named is named by where it stands, and until this it was named
        /// only when it woke up or when this player swung at it. Neither happens for the copy standing
        /// in a teammate's mine: the chunk streamed in long before anybody spoke about that rock, and
        /// the far player is the one hitting it. The message arrives, finds no spot of that name, and
        /// parks - forever, on a spot the player can see from where they stand.
        ///
        /// Position keys are the only ones this can serve: it matches a spot by computing the name it
        /// would be given. A savegame identity cannot be guessed from a live object, and does not need
        /// to be - those spots register themselves through the save system.
        ///
        /// Only while something is pending, and once a second even then, because it walks the loaded
        /// spots. Nothing is parked in an ordinary session, so this costs nothing in one.
        /// </summary>
        private static void LookForPending()
        {
            if (_pending.Count == 0) return;

            bool anyPosition = false;

            foreach (string key in _pending.Keys)
            {
                if (!ResourceWorkMessage.IsPositionKey(key)) continue;
                if (_spotByKey.ContainsKey(key)) continue;

                anyPosition = true;
                break;
            }

            if (!anyPosition) return;

            float now = UnityEngine.Time.unscaledTime;
            if (now - _lastLook < LookSeconds) return;

            _lastLook = now;

            try
            {
                foreach (Interact_Bhvr_ResourceSpot spot in
                    UnityEngine.Object.FindObjectsOfType<Interact_Bhvr_ResourceSpot>())
                {
                    // Names every unnamed one it passes, not only the one being looked for: they are
                    // all in hand, and a spot named now is a spot a teammate's next swing lands on
                    // rather than parks against.
                    NameByPosition(spot, late: true);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] looking for a spot a teammate named failed: " + e);
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

                SnapshotSent = sent;

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

            WorkingSpotKey = TryGetKey(__instance);
        }

        private static void MinigamePostfix(Interact_Bhvr_ResourceSpot __instance, int __state)
        {
            try
            {
                MinigameHits++;

                // A replay must never be reported back as local news.
                if (RemoteApply<WorkScope>.InProgress) return;
                if (!CoopSession.IsLive) return;

                int now = __instance.Charges;
                if (now == __state) return;

                if (!_keyBySpot.TryGetValue(__instance.GetInstanceID(), out string key))
                {
                    Refused++;

                    if (MayComplainAbout("spot#" + __instance.GetInstanceID()))
                    {
                        MelonLogger.Warning($"[Work] the spot '{__instance.name}' was worked here and " +
                            $"cannot be named across machines: {WhyUnaddressable(__instance)}. That " +
                            "swing, and any pile it drops, stays in this world.");
                    }

                    return;
                }

                byte charges = (byte)Math.Clamp(now, 0, byte.MaxValue);

                if (_lastSaid.TryGetValue(key, out byte said) && said == charges) return;

                _lastSaid[key] = charges;
                Worked++;

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
                if (_localWorkDepth == 0) WorkingSpotKey = null;
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
        /// <summary>
        /// Names a spot by where it stands, when nothing else names it.
        ///
        /// Only for spots with no <c>Saveable_ResourceSpot</c>. A spot that has one is registered by
        /// <see cref="RegisterPostfix"/> under its savegame identity, and that identity always wins:
        /// two names for one spot would let one machine speak about it in a way the other could not
        /// match, which is the same failure as no name at all.
        ///
        /// See <see cref="ResourceWorkMessage.MakePositionKey"/> for why a position is safe here where
        /// a dynamic save root's counter is not.
        /// </summary>
        private static void AwakePostfix(Interact_Bhvr_ResourceSpot __instance)
        {
            NameByPosition(__instance, late: false);
        }

        /// <summary>
        /// Mints and registers a spot's position name, or returns the one it already has.
        /// </summary>
        /// <param name="spot">The spot to name.</param>
        /// <param name="late">
        /// Whether this is the fallback asked at the moment somebody needs the name, rather than the
        /// pass at Awake. Counted separately: a session whose spots are all named late is a session
        /// where the Awake patch is not firing, and that is worth seeing rather than inferring.
        /// </param>
        /// <returns>The key, or null when this spot cannot be named this way.</returns>
        private static string? NameByPosition(Interact_Bhvr_ResourceSpot spot, bool late)
        {
            try
            {
                if (spot == null) return null;
                if (_keyBySpot.TryGetValue(spot.GetInstanceID(), out string known)) return known;

                // A spot the save system names is registered under its savegame identity, and that
                // identity always wins: two names for one spot would let one machine speak about it in
                // a way the other could not match, which is the same failure as no name at all.
                Saveable_ResourceSpot saveable = spot.GetComponent<Saveable_ResourceSpot>();
                if (saveable == null) saveable = spot.GetComponentInParent<Saveable_ResourceSpot>();
                if (saveable != null) return null;

                UnityEngine.Vector3 at = spot.transform.position;

                // A spot still sitting on the origin has not been placed yet. Naming it now would mint a
                // name every other spot awaiting placement would also answer to.
                if (at.x == 0f && at.y == 0f) return null;

                string key = ResourceWorkMessage.MakePositionKey(
                    (int)Math.Round(at.x), (int)Math.Round(at.y));

                if (!ResourceWorkMessage.IsReplicableKey(key)) return null;

                RegisterHits++;
                PlacedByPosition++;

                if (late)
                {
                    PlacedLate++;

                    if (PlacedLate == 1)
                    {
                        MelonLogger.Msg("[Work] a spot the save system does not name was named by where " +
                            "it stands, at the moment it was needed rather than when it woke up. Its " +
                            "work and its piles cross from here on.");
                    }
                }

                _keyBySpot[spot.GetInstanceID()] = key;
                _spotByKey[key] = spot;

                if (_pending.ContainsKey(key)) _justRegistered.Add(key);

                return key;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] naming a spot by its position failed: " + e);
                return null;
            }
        }

        private static void RegisterPostfix(Saveable_ResourceSpot __instance)
        {
            try
            {
                Interact_Bhvr_ResourceSpot spot = __instance._interactBhvr;
                if (spot == null)
                {
                    Unaddressable(__instance.name, "it carries no resource-spot behavior");
                    return;
                }

                ASaveRoot root = __instance.GetComponent<ASaveRoot>();
                if (root == null) root = __instance.GetComponentInParent<ASaveRoot>();

                if (root == null)
                {
                    Unaddressable(__instance.name, "nothing above it saves itself, so it has no name " +
                        "both machines would agree on");
                    return;
                }

                // Never ask a dynamic root for its key: producing one MINTS a per-savegame number as
                // a side effect, and those numbers alias between machines - this world's object 2 is
                // an unrelated object 2 over there. An aliased write corrupts something the player
                // was never near, which is far worse than a miss.
                SaveRoot_Dynamic dynamic = root.TryCast<SaveRoot_Dynamic>();

                if (dynamic != null && dynamic.IsDynamic())
                {
                    Unaddressable(__instance.name, "it saves under a dynamic root, whose numbering means " +
                        "something different in every savegame");
                    return;
                }

                string key = root.GetGameObjectInstanceKey();

                if (!ResourceWorkMessage.IsReplicableKey(key))
                {
                    Unaddressable(__instance.name, $"its save key '{Short(key ?? string.Empty)}' is not one " +
                        "a teammate could resolve");
                    return;
                }

                RegisterHits++;

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

                if (_pendingLive.Contains(key))
                {
                    // Mid-swing here: leave it parked and try again, the same as an arriving message
                    // does. The cursor is idempotent, so waiting costs nothing.
                    if (spot._isRunning) continue;

                    int before = spot.Charges;

                    if (before > charges)
                    {
                        Replay(spot, charges);
                        Applied++;

                        MelonLogger.Msg($"[Work] '{Short(key)}' caught up now that its spot has been " +
                            $"found: {before} -> {spot.Charges}.");
                    }
                    else
                    {
                        AlreadyThere++;
                    }

                    _pending.Remove(key);
                    _pendingLive.Remove(key);
                    continue;
                }

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
                    Refused++;
                    return;
                }

                if (!_enabled) return;

                if (!_spotByKey.TryGetValue(message.RootKey, out Interact_Bhvr_ResourceSpot spot) || spot == null)
                {
                    Park(message.RootKey, message.ChargesAfter, live: true);

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
                    Deferred++;
                    Park(message.RootKey, message.ChargesAfter, live: true);
                    return;
                }

                int before = spot.Charges;

                if (before <= message.ChargesAfter)
                {
                    AlreadyThere++;

                    if (before < message.ChargesAfter && MayComplainAbout(message.RootKey))
                    {
                        MelonLogger.Warning($"[Work] '{Short(message.RootKey)}' arrived " +
                            $"{message.ChargesBefore} -> {message.ChargesAfter} but this world held " +
                            $"{before}; kept {before}.");
                    }

                    return;
                }

                Replay(spot, message.ChargesAfter);

                Applied++;

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
            string? outerSpot = WorkingSpotKey;
            WorkingSpotKey = TryGetKey(spot);

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
                    WorkingSpotKey = outerSpot;
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

                Settled++;
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] settling a resource spot failed: " + e);
                return false;
            }
        }

        /// <param name="key">The spot's cross-machine name.</param>
        /// <param name="charges">The charge count the spot should end up at.</param>
        /// <param name="live">
        /// Whether this came from a teammate's swing. A live cursor is acted out when the spot turns
        /// up - the pile falls here too, and the tally moves here too, which is what the haul then
        /// reconciles. A snapshot cursor is settled in silence.
        /// </param>
        private static void Park(string key, byte charges, bool live)
        {
            if (!ResourceWorkMessage.IsReplicableKey(key)) return;

            _pending[key] = _pending.TryGetValue(key, out byte held) ? Math.Min(held, charges) : charges;

            if (live) _pendingLive.Add(key);

            if (_spotByKey.ContainsKey(key)) _justRegistered.Add(key);
        }

        /// <summary>
        /// Works out why a spot has no key, at the moment it is swung at.
        ///
        /// Asked here rather than only at registration, because the most likely answer is that
        /// registration never ran: the hooks are on <c>Saveable_ResourceSpot</c>, and a spot without one
        /// passes through this subsystem without ever being offered to it.
        /// </summary>
        private static string WhyUnaddressable(Interact_Bhvr_ResourceSpot spot)
        {
            try
            {
                Saveable_ResourceSpot saveable = spot.GetComponent<Saveable_ResourceSpot>();
                if (saveable == null) saveable = spot.GetComponentInParent<Saveable_ResourceSpot>();

                if (saveable == null)
                {
                    return "it has no Saveable_ResourceSpot, so it never registered and the save system " +
                        "keeps no per-spot record of it";
                }

                ASaveRoot root = spot.GetComponent<ASaveRoot>();
                if (root == null) root = spot.GetComponentInParent<ASaveRoot>();

                if (root == null) return "it is saveable but sits under no save root";

                SaveRoot_Dynamic dynamic = root.TryCast<SaveRoot_Dynamic>();
                if (dynamic != null && dynamic.IsDynamic()) return "its save root is dynamic";

                string key = root.GetGameObjectInstanceKey();

                return ResourceWorkMessage.IsReplicableKey(key)
                    ? $"its key '{Short(key ?? string.Empty)}' is replicable, so registration was missed"
                    : $"its key '{Short(key ?? string.Empty)}' is not replicable";
            }
            catch (Exception e)
            {
                return "the reason could not be determined: " + e.Message;
            }
        }

        /// <summary>
        /// Says why a spot cannot be named across machines.
        ///
        /// Every one of these used to return in silence, which left "the swing stayed in this world
        /// only" with four possible causes and no way to tell them apart from a log. The reason decides
        /// the remedy: a missing save root needs a different addressing scheme, a dynamic root is a
        /// refusal this mod will not lift.
        /// </summary>
        private static void Unaddressable(string name, string because)
        {
            Refused++;

            if (!MayComplainAbout("reg:" + name)) return;

            MelonLogger.Warning($"[Work] the spot '{name}' cannot be shared because {because}. Mining " +
                "it, and hauling what it drops, stays local.");
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
