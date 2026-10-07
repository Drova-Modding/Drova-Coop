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
    /// Shares swings at resource spots by replaying them, so each machine's vanilla code derives the sprite, pile and tally.
    /// The wire carries the absolute charge count afterwards and an apply only steps down to it, so redelivery and reordering are no-ops.
    /// </summary>
    internal static class ResourceWork
    {
        private const int MaxPerFrame = 32;

        private const float LookSeconds = 1f;

        private static readonly Dictionary<int, string> KeyBySpot = new();
        private static readonly Dictionary<string, Interact_Bhvr_ResourceSpot> SpotByKey = new(StringComparer.Ordinal);

        // Charge cursors for spots not streamed in yet, merged by minimum and never dropped.
        private static readonly Dictionary<string, byte> ParkedSwings = new(StringComparer.Ordinal);

        private static readonly List<string> JustRegistered = new();
        private static readonly Dictionary<string, byte> LastSaid = new(StringComparer.Ordinal);
        private static readonly Complaints ComplaintLog = new();

        [ThreadStatic]
        private static int _localWorkDepth;

        private static float _lastLook;

        // Parked cursors from a teammate's swing, acted out on arrival. Snapshot cursors are settled silently.
        private static readonly HashSet<string> PendingLive = new();
        private static uint _snapshotAnnounced;
        private static bool _snapshotComplete;
        private static uint _sequence;

        /// <summary>Whether this machine is inside its own player's swing right now.</summary>
        internal static bool IsLocalWorkInProgress => _localWorkDepth > 0;

        /// <summary>How many of the five hooks are in place.</summary>
        internal static int HooksInstalled { get; private set; }

        /// <summary>How many local swings were seen. Zero after a real swing means an inlined hook.</summary>
        internal static int MinigameHits { get; private set; }

        /// <summary>How many spots made themselves addressable.</summary>
        internal static int RegisterHits { get; private set; }

        /// <summary>How many swings were told to the session.</summary>
        internal static int Worked { get; private set; }

        /// <summary>How many teammates' swings were replayed here.</summary>
        internal static int Applied { get; private set; }

        /// <summary>How many spots were named by where they stand because the save system does not name them.</summary>
        internal static int PlacedByPosition { get; private set; }

        /// <summary>How many of those were named on demand rather than at Awake. All of them means the Awake patch never fires.</summary>
        internal static int PlacedLate { get; private set; }

        /// <summary>How many spots were handed their charge count without acting it out, so no pile and no tally.</summary>
        internal static int Settled { get; private set; }

        /// <summary>The cross-machine name of a spot, or null when it has none.</summary>
        internal static string? TryGetKey(Interact_Bhvr_ResourceSpot spot)
        {
            if (spot == null) return null;

            if (KeyBySpot.TryGetValue(spot.GetInstanceID(), out string key)) return key;

            // Named on demand too, because nothing may depend on the Awake pass having run.
            return NameByPosition(spot, late: true);
        }

        /// <summary>
        /// The key of the spot being swung at or replayed right now, so the haul can attribute a tally write to its spot.
        /// </summary>
        internal static string? WorkingSpotKey { get; private set; }

        /// <summary>How many arrived at a spot already that far along.</summary>
        internal static int AlreadyThere { get; private set; }

        /// <summary>How many are waiting for their chunk to stream in.</summary>
        internal static int Pending => ParkedSwings.Count;

        /// <summary>How many were held because the spot was mid-interaction.</summary>
        internal static int Deferred { get; private set; }

        /// <summary>How many were refused outright. Should be zero.</summary>
        internal static int Refused { get; private set; }

        /// <summary>How many spots are addressable here.</summary>
        internal static int Registered => SpotByKey.Count;

        /// <summary>How many spot records the host sent a joining player.</summary>
        internal static int SnapshotSent { get; private set; }

        /// <summary>How many arrived from the host on joining.</summary>
        internal static int SnapshotReceived { get; private set; }

        /// <summary>Whether teammates' swings are replayed here at all.</summary>
        internal static bool Enabled { get; set; } = true;

        /// <summary>Forgets what this session knew. Applied work is never rolled back.</summary>
        internal static void Reset()
        {
            KeyBySpot.Clear();
            SpotByKey.Clear();
            ParkedSwings.Clear();
            PendingLive.Clear();
            JustRegistered.Clear();
            LastSaid.Clear();
            ComplaintLog.Clear();

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

        /// <summary>Applies the patches.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            int applied = 0;

            // The swing, on the delegate-registered listener rather than the private handler it calls, which may be inlined.
            if (Hooking.TryPrefix(harmony, typeof(Interact_Bhvr_ResourceSpot),
                nameof(Interact_Bhvr_ResourceSpot.MinigameFinishedEventListener),
                typeof(ResourceWork), nameof(MinigamePrefix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Interact_Bhvr_ResourceSpot),
                nameof(Interact_Bhvr_ResourceSpot.MinigameFinishedEventListener),
                typeof(ResourceWork), nameof(MinigamePostfix))) applied++;

            // A spot becoming addressable, by two paths so one being inlined away does not take the subsystem with it.
            if (Hooking.TryPostfix(harmony, typeof(Saveable_ResourceSpot),
                nameof(Saveable_ResourceSpot.LoadCallback),
                typeof(ResourceWork), nameof(RegisterPostfix))) applied++;

            if (Hooking.TryPostfix(harmony, typeof(Saveable_ResourceSpot),
                nameof(Saveable_ResourceSpot.Start),
                typeof(ResourceWork), nameof(RegisterPostfix))) applied++;

            // Spots the save system never names have no saveable component, so the behavior's own Awake is used.
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

        /// <summary>Applies what has arrived and hands pending cursors to spots that just streamed in. Once per frame.</summary>
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
                    $"resource spot(s) arrived; {SpotByKey.Count} spot(s) are loaded here and the " +
                    "rest are held until their part of the world streams in.");
            }
        }

        // Names loaded spots by position while a position key is parked, else a teammate's swing could park forever.
        // Once a second at most, because it walks every loaded spot.
        private static void LookForPending()
        {
            if (ParkedSwings.Count == 0) return;

            bool anyPosition = false;

            foreach (string key in ParkedSwings.Keys)
            {
                if (!ResourceWorkMessage.IsPositionKey(key)) continue;
                if (SpotByKey.ContainsKey(key)) continue;

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
                    NameByPosition(spot, late: true);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] looking for a spot a teammate named failed: " + e);
            }
        }

        /// <summary>The host has finished sending the spot table.</summary>
        internal static void CompleteSnapshot(uint recordCount)
        {
            _snapshotAnnounced = recordCount;
            _snapshotComplete = true;
        }

        /// <summary>
        /// Sends a joining player every spot's charges, read from the savegame because live objects cover only streamed chunks.
        /// </summary>
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

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Il2CppSystem.Object> pair in data)
                {
                    string key = pair.Key;
                    if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal)) continue;

                    string instanceKey = key[prefix.Length..];
                    if (!ResourceWorkMessage.IsReplicableKey(instanceKey)) continue;

                    SaveData_GenericDictionary objectData = pair.Value?.TryCast<SaveData_GenericDictionary>();
                    if (objectData == null) continue;

                    SaveData_Int charges = objectData.GetGenericData("Saveable_ResourceSpot")
                        ?.TryCast<SaveData_Int>();

                    if (charges == null) continue;

                    records.Add(new ResourceSpotSnapshotMessage.Record
                    {
                        RootKey = instanceKey,
                        Charges = (byte)Math.Clamp(charges.Value, 0, byte.MaxValue),
                    });
                }

                foreach (ResourceSpotSnapshotMessage.Record[] chunk in records.Chunk(ResourceSpotSnapshotMessage.MaxRecords))
                {
                    NetworkAccess.Send(peer, new ResourceSpotSnapshotMessage { Records = chunk });
                }

                int sent = records.Count;

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

                if (!KeyBySpot.TryGetValue(__instance.GetInstanceID(), out string key))
                {
                    Refused++;

                    if (ComplaintLog.MayComplainAbout("spot#" + __instance.GetInstanceID()))
                    {
                        MelonLogger.Warning($"[Work] the spot '{__instance.name}' was worked here and " +
                            $"cannot be named across machines: {WhyUnaddressable(__instance)}. That " +
                            "swing, and any pile it drops, stays in this world.");
                    }

                    return;
                }

                byte charges = (byte)Math.Clamp(now, 0, byte.MaxValue);

                if (LastSaid.TryGetValue(key, out byte said) && said == charges) return;

                LastSaid[key] = charges;
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

                MelonLogger.Msg($"[Work] worked '{Complaints.Short(key)}' ({Name(__instance)}): {__state} -> {now}.");
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

        private static void AwakePostfix(Interact_Bhvr_ResourceSpot __instance)
        {
            NameByPosition(__instance, late: false);
        }

        // Names a spot without a Saveable_ResourceSpot by where it stands. Late means named on demand rather than at Awake.
        private static string? NameByPosition(Interact_Bhvr_ResourceSpot spot, bool late)
        {
            try
            {
                if (spot == null) return null;
                if (KeyBySpot.TryGetValue(spot.GetInstanceID(), out string known)) return known;

                // A saveable spot's savegame identity always wins, since two names for one spot cannot be matched.
                Saveable_ResourceSpot saveable = spot.GetComponent<Saveable_ResourceSpot>();
                if (saveable == null) saveable = spot.GetComponentInParent<Saveable_ResourceSpot>();
                if (saveable != null) return null;

                UnityEngine.Vector3 at = spot.transform.position;

                // A spot on the origin has not been placed yet and would share its name with every other one.
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

                KeyBySpot[spot.GetInstanceID()] = key;
                SpotByKey[key] = spot;

                if (ParkedSwings.ContainsKey(key)) JustRegistered.Add(key);

                return key;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] naming a spot by its position failed: " + e);
                return null;
            }
        }

        // Registration only. The loading flag is up during the load callback, so an apply here would never reach the save.
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

                // Never ask a dynamic root for its key. That mints a per-savegame number that aliases an unrelated object on the peer.
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
                    Unaddressable(__instance.name, $"its save key '{Complaints.Short(key ?? string.Empty)}' is not one " +
                        "a teammate could resolve");
                    return;
                }

                RegisterHits++;

                KeyBySpot[spot.GetInstanceID()] = key;
                SpotByKey[key] = spot;

                if (ParkedSwings.ContainsKey(key)) JustRegistered.Add(key);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] registering a resource spot failed: " + e);
            }
        }

        private static void DrainRegistrations()
        {
            if (JustRegistered.Count == 0) return;

            for (int index = 0; index < JustRegistered.Count; index++)
            {
                string key = JustRegistered[index];

                if (!ParkedSwings.TryGetValue(key, out byte charges)) continue;
                if (!SpotByKey.TryGetValue(key, out Interact_Bhvr_ResourceSpot spot) || spot == null) continue;

                if (PendingLive.Contains(key))
                {
                    // Mid-swing here, so it stays parked.
                    if (spot._isRunning) continue;

                    int before = spot.Charges;

                    if (before > charges)
                    {
                        Replay(spot, charges);
                        Applied++;

                        MelonLogger.Msg($"[Work] '{Complaints.Short(key)}' caught up now that its spot has been " +
                            $"found: {before} -> {spot.Charges}.");
                    }
                    else
                    {
                        AlreadyThere++;
                    }

                    ParkedSwings.Remove(key);
                    PendingLive.Remove(key);
                    continue;
                }

                if (ApplySilently(spot, charges))
                {
                    ParkedSwings.Remove(key);
                }
            }

            JustRegistered.Clear();
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

                if (!Enabled) return;

                if (!SpotByKey.TryGetValue(message.RootKey, out Interact_Bhvr_ResourceSpot spot) || spot == null)
                {
                    Park(message.RootKey, message.ChargesAfter, live: true);

                    if (ComplaintLog.MayComplainAbout(message.RootKey))
                    {
                        MelonLogger.Msg($"[Work] '{Complaints.Short(message.RootKey)}' is not streamed in here; " +
                            $"held at {message.ChargesAfter} until it loads.");
                    }

                    return;
                }

                // Mid-swing here. Stepping charges under it would fight the local minigame.
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

                    if (before < message.ChargesAfter && ComplaintLog.MayComplainAbout(message.RootKey))
                    {
                        MelonLogger.Warning($"[Work] '{Complaints.Short(message.RootKey)}' arrived " +
                            $"{message.ChargesBefore} -> {message.ChargesAfter} but this world held " +
                            $"{before}; kept {before}.");
                    }

                    return;
                }

                Replay(spot, message.ChargesAfter);

                Applied++;

                MelonLogger.Msg($"[Work] player {message.Player} worked '{Complaints.Short(message.RootKey)}' " +
                    $"({Name(spot)}): {before} -> {spot.Charges}.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Work] applying a teammate's swing failed: " + e);
            }
        }

        // Replays a teammate's swings as swings, so every consequence runs through this machine's own code.
        private static void Replay(Interact_Bhvr_ResourceSpot spot, byte target)
        {
            string? outerSpot = WorkingSpotKey;
            WorkingSpotKey = TryGetKey(spot);

            using (RemoteApply<WorkScope>.Enter())
            {
                Actor worker = PlayerAccess.TryGetPlayer(out Actor local) ? local : null!;

                // The rubble listener paints only when its _actor is a player, which only an interaction sets.
                Interact_Bhvr_Spot_Rubble rubble = spot.GetComponent<Interact_Bhvr_Spot_Rubble>();
                Actor saved = rubble != null ? rubble._actor : null!;

                if (rubble != null) rubble._actor = worker;

                try
                {
                    // Not SetCharges, which fires an extra not-worked event per step and doubles the writeback.
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

        // Sets charges without acting them out, so no pile spawns and no tally moves. False when the spot could not be moved.
        private static bool ApplySilently(Interact_Bhvr_ResourceSpot spot, byte target)
        {
            try
            {
                if (spot._isRunning) return false;
                if (spot.Charges <= target) return true;

                using (RemoteApply<WorkScope>.Enter())
                {
                    spot.SetCharges(target, false);

                    // Repaint from the local state, since a synthesized zero would deactivate single-sprite rock faces.
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

        // Live cursors come from a teammate's swing and are acted out when the spot turns up.
        private static void Park(string key, byte charges, bool live)
        {
            if (!ResourceWorkMessage.IsReplicableKey(key)) return;

            ParkedSwings[key] = ParkedSwings.TryGetValue(key, out byte held) ? Math.Min(held, charges) : charges;

            if (live) PendingLive.Add(key);

            if (SpotByKey.ContainsKey(key)) JustRegistered.Add(key);
        }

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
                    ? $"its key '{Complaints.Short(key ?? string.Empty)}' is replicable, so registration was missed"
                    : $"its key '{Complaints.Short(key ?? string.Empty)}' is not replicable";
            }
            catch (Exception e)
            {
                return "the reason could not be determined: " + e.Message;
            }
        }

        private static void Unaddressable(string name, string because)
        {
            Refused++;

            if (!ComplaintLog.MayComplainAbout("reg:" + name)) return;

            MelonLogger.Warning($"[Work] the spot '{name}' cannot be shared because {because}. Mining " +
                "it, and hauling what it drops, stays local.");
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
