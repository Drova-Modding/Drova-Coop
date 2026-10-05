using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using Il2Cpp;
using DrovaCoopMod.Messages;
using MelonLoader;

namespace DrovaCoopMod.Session
{
    /// <summary>
    /// The mod's session layer: it turns the API's transport events into a state a UI can render, keeps
    /// the roster of who is in, settles the version handshake, and owns player identity. Everything above
    /// this - world sync, menus, lobby - builds on it; everything below it belongs to the Drova Modding API.
    ///
    /// All of it runs on the Unity main thread, because that is where the transport delivers.
    /// </summary>
    public static class CoopSession
    {
        /// <summary>
        /// The coop protocol version, defined in the protocol assembly so that things which are not the
        /// game - the headless peer, the tests - can speak the same handshake. Kept here as the name the
        /// session layer reads.
        /// </summary>
        public const ushort ProtocolVersion = CoopProtocol.Version;

        /// <summary>
        /// How long a join may sit in <see cref="CoopSessionState.Connecting"/> before it is given up on.
        /// Covers both halves of getting in: the transport connecting, and the host answering the
        /// handshake. A host without this mod installed accepts the connection and then says nothing at
        /// all, so without a deadline the client waits forever with no way to tell what went wrong.
        /// </summary>
        public const float ConnectTimeoutSeconds = 15f;

        /// <summary>
        /// How often each player measures the round trip to the others. Often enough that a number on
        /// screen is current, rare enough that the measurement is not itself part of the traffic.
        /// </summary>
        private const float PingIntervalSeconds = 2f;

        /// <summary>
        /// How long an unanswered ping is kept before it is forgotten. A reply arriving later than this
        /// would report a round trip that says more about the timeout than about the link.
        /// </summary>
        private const float PingExpirySeconds = 10f;

        /// <summary>
        /// How many unapplied blows are held before the oldest is dropped.
        ///
        /// There is a window where reports arrive and nothing drains them - between joining and the world
        /// being up, or across a scene change - and an unbounded queue would spend it collecting damage to
        /// deliver all at once to a player who has just regained a body. Dropping the oldest keeps the most
        /// recent fight, which is the one the player is in.
        /// </summary>
        private const int MaxPendingDamage = 32;

        /// <summary>
        /// How many unlearned things are held before further ones are refused.
        ///
        /// Far larger than <see cref="MaxPendingDamage"/> and used differently - see the enqueue for why
        /// nothing here may be dropped to make room. Two players cannot find this many recipes and letters
        /// in the time it takes a world to finish loading, so reaching it means something is wrong rather
        /// than busy.
        /// </summary>
        private const int MaxPendingKnowledge = 256;

        /// <summary>
        /// How many unpaid kills are held before further ones are refused.
        ///
        /// Larger than the knowledge cap, because unlike a recipe this is something a busy session produces
        /// steadily: two players clearing a camp while a third is still loading generate one of these per
        /// creature. Refused rather than dropped for the same reason - nothing restates a payout.
        /// </summary>
        private const int MaxPendingExperience = 512;

        /// <summary>
        /// How many unapplied deaths are held before further ones are refused.
        ///
        /// Refused rather than dropped, for the same reason as <see cref="MaxPendingKnowledge"/> and more
        /// sharply: a death is never repeated by anything, and one that is lost leaves a creature alive on
        /// one screen for the rest of the session and in one of the two saves afterwards.
        ///
        /// Larger than the knowledge cap because a queue of deaths is a normal state rather than a
        /// suspicious one - a player clearing a camp at the far end of the world produces a burst of them
        /// while this machine has not streamed a single body to apply them to.
        /// </summary>
        private const int MaxPendingEnemyDeaths = 512;

        /// <summary>
        /// The gvar queue's own ceiling, above the others because a join snapshot legitimately delivers
        /// a whole playthrough's moved variables at once.
        /// </summary>
        private const int MaxPendingGVarChanges = 4096;

        /// <summary>
        /// Sized to the variable database rather than to live traffic: a late-game world has moved more
        /// than 4096 of its ~6200 variables, and the catch-up arrives all at once. Past the cap the tail
        /// lists would never arrive at all.
        /// </summary>
        private const int MaxPendingGVarCatchup = 16384;

        private static readonly Dictionary<PlayerId, string> _players = new();
        private static readonly Dictionary<PlayerId, PlayerStateMessage> _latestStates = new();
        private static readonly Dictionary<PlayerId, EquipmentSnapshot> _equipment = new();

        /// <summary>Each other player's learned talents, and how often they changed.</summary>
        private static readonly Dictionary<PlayerId, (string[] Guids, int Version)> _talents = new();

        private static readonly Dictionary<PlayerId, PlayerVitalsMessage> _vitals = new();
        private static readonly Dictionary<PlayerId, ItemUseSnapshot> _itemUses = new();

        // Host only. A transport peer id means nothing on another machine, so the host is the only thing
        // that can translate between the two, and it is the only thing that ever needs to.
        private static readonly Dictionary<int, PlayerId> _peerToPlayer = new();

        // Reused so relaying state does not allocate a list per player per frame.
        private static readonly List<INetPeer> _peerBuffer = new();

        private static readonly Dictionary<PlayerId, float> _roundTripMs = new();
        private static readonly Dictionary<uint, double> _pingSentAt = new();

        // Damage waiting to be applied to the local player, and the highest blow already counted from each
        // attacker. The second is what makes a repeated report harmless; see DamageReportMessage.Sequence.
        private static readonly Queue<DamageReportMessage> _incomingDamage = new();
        private static readonly Dictionary<PlayerId, uint> _lastDamageSequence = new();

        // The same pair for status effects, and for a sharper version of the same reason: applying an
        // effect twice is a second stack of poison rather than a harmless repeat.
        private static readonly Queue<StatusEffectMessage> _incomingEffects = new();
        private static readonly Dictionary<PlayerId, byte> _lastStatusSequence = new();

        // Things learned elsewhere that this player has not been taught yet. No dedup counter, unlike the
        // two above: the game refuses a recipe it already has, so a repeat costs nothing.
        private static readonly Queue<KnowledgeMessage> _incomingKnowledge = new();

        // Creatures somebody else was paid for that this player has not been. The dedup is not here but in
        // ExperienceShare, because the same set has to refuse a payout this machine already made itself.
        private static readonly Queue<ExperienceMessage> _incomingExperience = new();

        // Which handle each conjured creature answers to. Held rather than applied on arrival, because the
        // body it names may not have been spawned here yet - a conjure can carry a delay.
        private static readonly Queue<SummonBindMessage> _incomingSummonBinds = new();

        // Creatures somebody killed. The set is what makes this idempotent in both directions: it stops
        // the same death being sent twice from here and stops one being applied twice when it arrives
        // twice, which matters because the host relays and a third player's copy comes back around.
        private static readonly Queue<EnemyDeathMessage> _incomingEnemyDeaths = new();
        private static readonly HashSet<string> _deadEnemies = new(StringComparer.Ordinal);

        // The client's side of the host's claims. The claimed set itself is EnemyRegistry.ClaimedIds.
        private static readonly Queue<EnemyBindMessage> _incomingBinds = new();
        private static readonly Queue<EnemyReleaseMessage> _incomingReleases = new();
        private static readonly Queue<EnemyGraveyardMessage> _incomingGraveyard = new();
        private static readonly Queue<EnemyAliveMessage> _incomingAlive = new();

        private static readonly Queue<EnemyReviveMessage> _incomingRevive = new();

        private static readonly Queue<GVarChangeMessage> _incomingGVars = new();

        private static readonly Queue<GVarChangeMessage> _incomingGVarCatchup = new();

        private static readonly Queue<ResourceWorkMessage> _incomingResourceWork = new();

        private static readonly Queue<ResourceSpotSnapshotMessage> _incomingResourceSnapshot = new();

        private const int MaxPendingLootSnapshots = 64;

        private static readonly Queue<LootSnapshotMessage> _incomingLootSnapshot = new();

        private static readonly Queue<HaulPileMessage> _incomingHaulPiles = new();

        /// <summary>
        /// Whether the catch-up now in flight was asked for with its consequences re-run, even for
        /// values this world already holds. Remembered here because the request and the terminator
        /// that acts on it are two different messages.
        /// </summary>
        private static bool _resyncReplaysUnchanged;

        private static readonly Queue<CutsceneMessage> _incomingCutscenes = new();
        private static readonly Queue<ActorPortMessage> _incomingActorPorts = new();

        private static readonly Queue<WeatherMessage> _incomingWeather = new();

        private static readonly Queue<JournalEntryMessage> _incomingJournal = new();

        private static readonly Queue<JournalTopicMessage> _incomingJournalTopics = new();

        private static readonly Queue<NpcPlacementAnswerMessage> _incomingNpcPlacements = new();

        private static readonly Queue<DaytimeMessage> _incomingDaytime = new();

        private static readonly Queue<MapUnlockMessage> _incomingMapUnlocks = new();

        private static readonly Queue<DialogueExpMessage> _incomingDialogueExp = new();
        private static readonly Queue<DialogueItemsMessage> _incomingDialogueItems = new();
        private static readonly Queue<EnemyHealthMessage> _incomingEnemyHealth = new();
        private static readonly Queue<EnemySnapshotMessage> _incomingSnapshots = new();
        private static readonly Queue<EnemySpawnMessage> _incomingSpawns = new();
        private static readonly Queue<EnemyDriveMessage> _incomingDrives = new();

        // Host only: blows clients reported, and the highest already counted from each attacker so a
        // resend is harmless.
        private static readonly Queue<EnemyDamageMessage> _incomingEnemyDamage = new();
        private static readonly Dictionary<PlayerId, uint> _lastEnemyDamageSequence = new();
        private static readonly Queue<EnemyEngageMessage> _incomingEnemyEngage = new();
        private static readonly Queue<EnemyEngageMessage> _incomingHostEngage = new();
        private static readonly Queue<EnemyDamageMessage> _incomingLeasedDamage = new();

        // Host only: clients asking for the world to be turned over because they slept. Kept short on
        // purpose - a backlog of these is several nights, and nobody wants to wake up the day after
        // tomorrow because two asks queued behind a stall.
        private static readonly Queue<DayAdvanceRequestMessage> _incomingDayAdvance = new();
        private static uint _nextEnemyDamageSequence;
        private static uint _lastLeasedDamageSequence;

        // Reused so answering a world-state ask does not allocate a chunk's worth of strings per message.
        private static readonly List<string> _graveyardChunk = new();
        private static readonly List<string> _aliveChunk = new();

        // A real clock, not accumulated frame time. Timing a round trip by adding up per-frame deltas
        // measures in whole frames: every result comes back a multiple of the frame duration, so a 60 fps
        // game cannot report anything between 17 ms and 33 ms and the number describes the framerate more
        // than the link. The reply is still only noticed on a frame boundary, because that is when the
        // transport is pumped, but that adds under one frame instead of quantizing the whole measurement.
        private static readonly System.Diagnostics.Stopwatch _clock = new();

        // The undo of every message registration, recorded by the registration itself so Shutdown cannot
        // miss one.
        private static readonly List<Action> _unregisters = new();

        private static bool _initialized;
        private static float _connectingSeconds;
        private static float _sincePing;
        private static uint _nextPingToken;
        private static uint _nextDamageSequence;

        // Counted per target rather than once for everybody. The receiver's guard is a per-source high
        // water mark with a half-range window, so a single counter shared across targets races ahead
        // while this player fights somebody else and the next effect they send here reads as one that
        // arrived late - silently dropped, and again every time the counter laps.
        private static readonly Dictionary<PlayerId, byte> _nextStatusSequence = new();

        /// <summary>
        /// Where the local player is in getting into a session.
        ///
        /// Written through a setter that tells the API, rather than assigned at each transition. Other
        /// mods gate real decisions on this - whether to spawn, whether to keep an event running - and they
        /// learn about it through <c>CoopAccess.OnSessionChanged</c>. Announcing from the property is what
        /// makes that true of every transition including the ones added later; announcing at each assignment
        /// is what made it true of none of them, because the call was simply never written.
        /// </summary>
        public static CoopSessionState State
        {
            get => _state;
            private set
            {
                if (_state == value) return;

                _state = value;

                CoopProvider.NotifyChanged();
            }
        }

        private static CoopSessionState _state = CoopSessionState.Idle;

        /// <summary>Whether a session is up, as host or as joined client.</summary>
        internal static bool IsLive => State is CoopSessionState.Hosting or CoopSessionState.Joined;

        /// <summary>
        /// Who the local player is in this session. <see cref="PlayerId.Host"/> when hosting, whatever the
        /// host assigned when joined, and <see cref="PlayerId.None"/> outside a session.
        /// </summary>
        public static PlayerId LocalPlayerId { get; private set; } = PlayerId.None;

        /// <summary>
        /// Whether players may damage each other in this session.
        ///
        /// The host's own setting when hosting, and whatever the host said at the handshake when joined -
        /// never the local preference on a client. A rule both machines have to agree on cannot be read
        /// twice, because disagreement here is not symmetric: it would leave one player hittable by
        /// somebody they could not hit back.
        ///
        /// False outside a session, which is what makes it safe to consult from anything that builds a
        /// body before the session state has settled.
        ///
        /// Changeable while a session runs: the host watches its own setting and tells everyone when it
        /// moves. Whoever acts on this must therefore keep watching it rather than reading it once - the
        /// bodies do, by reconciling their hostility every frame against this value.
        /// </summary>
        public static bool FriendlyFire { get; private set; }

        /// <summary>
        /// How much health a downed player stands up with, as a percentage of their maximum.
        ///
        /// The host's setting, adopted by everyone else, for the same reason
        /// <see cref="FriendlyFire"/> is: a player who recovered to more health than their opponent would
        /// win fights they should have lost, and nothing on either screen would say why.
        ///
        /// Falls back to <see cref="CoopProtocol.DefaultRecoveryHealthPercent"/> outside a session and
        /// whenever a host sends zero, so the value is always one a body can be stood up on.
        /// </summary>
        public static byte RecoveryHealthPercent { get; private set; } = CoopProtocol.DefaultRecoveryHealthPercent;

        /// <summary>
        /// How tough creatures are, as a percentage of their authored health.
        ///
        /// The host's setting, adopted by everyone, because a creature is one fight for the whole party:
        /// whichever machine resolves a blow on it scales that blow, and two machines with different
        /// numbers would have the same creature take a different beating depending on who struck it.
        /// See <see cref="World.EnemyScaling"/> for what acts on this.
        ///
        /// Falls back to <see cref="CoopProtocol.DefaultEnemyHealthPercent"/> outside a session and
        /// whenever a host sends zero, so single player is always the game as shipped.
        /// </summary>
        public static ushort EnemyHealthPercent { get; private set; } = CoopProtocol.DefaultEnemyHealthPercent;

        /// <summary>
        /// The loot rule in force, as <see cref="CoopProtocol.LootModeInstanced"/> or
        /// <see cref="CoopProtocol.LootModeShared"/>. The host's setting, adopted by every client; see
        /// <see cref="World.LootShare"/> for what it does.
        /// </summary>
        public static byte LootMode { get; private set; } = CoopProtocol.LootModeInstanced;

        /// <summary>
        /// Why the last attempt failed, in words meant for a player. Null unless
        /// <see cref="State"/> is <see cref="CoopSessionState.Failed"/>.
        /// </summary>
        public static string? LastError { get; private set; }

        /// <summary>
        /// How many state updates this session has refused for carrying numbers no body can be given -
        /// a NaN or an absurd coordinate. Zero on a healthy session; anything else is a peer sending
        /// nonsense, whether through a bug or on purpose, and this is where that becomes visible.
        /// </summary>
        public static int ImplausibleStates { get; private set; }

        /// <summary>
        /// The code others need to join, once a relay session is up. Null for a direct session.
        /// </summary>
        public static string? SessionCode => NetworkAccess.SessionCode;

        /// <summary>
        /// Whether the traffic is sealed against the relay. Worth showing in a UI: it tells the player
        /// whether the relay operator can read their session.
        /// </summary>
        public static bool IsEncrypted => NetworkAccess.IsEncrypted;

        /// <summary>
        /// Everyone in the session, including the local player, keyed by the id the host assigned. Every
        /// machine agrees on these keys, which is the whole point of them not being transport peer ids.
        /// </summary>
        public static IReadOnlyDictionary<PlayerId, string> Players => _players;

        /// <summary>
        /// The most recent state each player sent. Nothing applies these to the world yet - that is where
        /// the actual coop work starts.
        /// </summary>
        public static IReadOnlyDictionary<PlayerId, PlayerStateMessage> LatestStates => _latestStates;

        /// <summary>
        /// What each other player has equipped, as item ids, with a count of how many times it has
        /// changed. Applying equipment tears a body's slots down and builds them back up, so whoever
        /// applies it needs to know when there is something new rather than doing that every frame.
        /// </summary>
        public static IReadOnlyDictionary<PlayerId, EquipmentSnapshot> Equipment => _equipment;

        /// <summary>Each other player's learned talents, with a version that counts their changes.</summary>
        public static IReadOnlyDictionary<PlayerId, (string[] Guids, int Version)> Talents => _talents;

        /// <summary>
        /// What each far player's bars are out of, as last told. Absent until their first
        /// <see cref="PlayerVitalsMessage"/>, which the sender puts out on its first tick in a session and
        /// again whenever the roster grows.
        /// </summary>
        public static IReadOnlyDictionary<PlayerId, PlayerVitalsMessage> Vitals => _vitals;

        /// <summary>
        /// The last ability each other player announced, with a count of how many announcements have
        /// arrived. Only the newest is kept: a cast that has been superseded before its body could
        /// perform it is a spell nobody will see anyway, and queueing them would have a body work through
        /// a backlog of spells long after their owner stopped casting.
        /// </summary>
        public static IReadOnlyDictionary<PlayerId, ItemUseSnapshot> ItemUses => _itemUses;

        /// <summary>
        /// Registers the message types and subscribes to the transport. Safe to call more than once; only
        /// the first call does anything, because registering an id twice is an error by design.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;

            Register<HandshakeMessage>(MessageIds.Handshake, OnHandshake);
            Register<HandshakeResultMessage>(MessageIds.HandshakeResult, OnHandshakeResult);
            Register<RosterSnapshotMessage>(MessageIds.RosterSnapshot, OnRosterSnapshot);
            Register<PlayerJoinedMessage>(MessageIds.PlayerJoined, OnPlayerJoined);
            Register<PlayerLeftMessage>(MessageIds.PlayerLeft, OnPlayerLeft);
            Register<SessionRulesMessage>(MessageIds.SessionRules, OnSessionRules);
            Register<PlayerStateMessage>(MessageIds.PlayerState, OnPlayerState);
            Register<PingMessage>(MessageIds.Ping, OnPing);
            Register<PlayerEquipmentMessage>(MessageIds.PlayerEquipment, OnEquipment);
            Register<PlayerTalentsMessage>(MessageIds.PlayerTalents, OnTalents);
            Register<DamageReportMessage>(MessageIds.DamageReport, OnDamageReport);
            Register<ItemUseMessage>(MessageIds.ItemUse, OnItemUse);
            Register<StatusEffectMessage>(MessageIds.StatusEffect, OnStatusEffect);
            Register<KnowledgeMessage>(MessageIds.Knowledge, OnKnowledge);
            Register<ExperienceMessage>(MessageIds.Experience, OnExperience);
            Register<SummonBindMessage>(MessageIds.SummonBind, (peer, m) =>
            {
                if (m.IsPlausible()) QueueFromHost(m, _incomingSummonBinds, MaxPendingEnemyDeaths, "conjured creatures");
            });
            Register<EnemyDeathMessage>(MessageIds.EnemyDeath, OnEnemyDeath);
            Register<EnemyBindMessage>(MessageIds.EnemyBind,
                (peer, m) => QueueFromHost(m, _incomingBinds, MaxPendingEnemyDeaths, "creature claims"));
            Register<EnemyReleaseMessage>(MessageIds.EnemyRelease,
                (peer, m) => QueueFromHost(m, _incomingReleases, MaxPendingEnemyDeaths, "creature releases"));
            Register<EnemyDamageMessage>(MessageIds.EnemyDamage, OnEnemyDamage);
            Register<EnemyEngageMessage>(MessageIds.EnemyEngage, OnEnemyEngage);
            Register<DayAdvanceRequestMessage>(MessageIds.DayAdvanceRequest, OnDayAdvanceRequest);
            Register<EnemyHealthMessage>(MessageIds.EnemyHealth, OnEnemyHealth);
            Register<EnemyGraveyardMessage>(MessageIds.EnemyGraveyard,
                (peer, m) => QueueFromHost(m, _incomingGraveyard, MaxPendingEnemyDeaths, "graveyard chunks"));
            Register<EnemyAliveMessage>(MessageIds.EnemyAlive,
                (peer, m) => QueueFromHost(m, _incomingAlive, MaxPendingEnemyDeaths, "alive answers"));
            Register<EnemyReviveMessage>(MessageIds.EnemyRevive,
                (peer, m) => QueueFromHost(m, _incomingRevive, MaxPendingEnemyDeaths, "revive decrees"));

            // Never relayed back to the sender. Arriving assertions do more than write a value, and a world
            // reacting to its own story a second time is a story told twice.
            Register<GVarChangeMessage>(MessageIds.GVarChange,
                (peer, m) => RelayAndQueue(peer, m, _incomingGVars, MaxPendingGVarChanges, "gvar changes"));
            Register<GVarSnapshotMessage>(MessageIds.GVarSnapshot, OnGVarSnapshot);
            Register<GVarSnapshotEndMessage>(MessageIds.GVarSnapshotEnd, OnGVarSnapshotEnd);
            Register<GVarResyncRequestMessage>(MessageIds.GVarResyncRequest, OnGVarResyncRequest);

            // Stamped with who actually swung, because the client cannot be trusted to name itself and the
            // log line is only worth anything if the name is right.
            Register<ResourceWorkMessage>(MessageIds.ResourceWork, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting && _peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) m.Player = sender.Value;

                RelayAndQueue(peer, m, _incomingResourceWork, MaxPendingEnemyDeaths, "resource work");
            });
            Register<ResourceSpotSnapshotMessage>(MessageIds.ResourceSpotSnapshot, (peer, m) =>
            {
                if (m.Records != null) QueueFromHost(m, _incomingResourceSnapshot, MaxPendingEnemyDeaths, "resource spot records");
            });
            Register<ResourceSpotSnapshotEndMessage>(MessageIds.ResourceSpotSnapshotEnd, OnResourceSpotSnapshotEnd);

            // Stamped with who actually hauled it, for the same reason resource work is.
            Register<HaulPileMessage>(MessageIds.HaulPile, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting && _peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) m.Player = sender.Value;

                RelayAndQueue(peer, m, _incomingHaulPiles, MaxPendingEnemyDeaths, "hauled piles");
            });

            // Never relayed back to the sender, because replaying a cutscene twice is not a no-op.
            Register<CutsceneMessage>(MessageIds.Cutscene,
                (peer, m) => RelayAndQueue(peer, m, _incomingCutscenes, MaxPendingEnemyDeaths, "cutscene starts"));
            Register<ActorPortMessage>(MessageIds.ActorPort,
                (peer, m) => RelayAndQueue(peer, m, _incomingActorPorts, MaxPendingEnemyDeaths, "cutscene actor ports"));
            Register<WeatherMessage>(MessageIds.Weather, OnWeather);

            // The sender is excluded not for safety, since triggering an entry twice pays nothing twice, but
            // because the echo is pure noise.
            Register<JournalEntryMessage>(MessageIds.JournalEntry,
                (peer, m) => RelayAndQueue(peer, m, _incomingJournal, MaxPendingEnemyDeaths, "journal entries"));
            Register<JournalTopicMessage>(MessageIds.JournalTopic,
                (peer, m) => RelayAndQueue(peer, m, _incomingJournalTopics, MaxPendingEnemyDeaths, "completion marks"));
            Register<NpcPlacementRequestMessage>(MessageIds.NpcPlacementRequest, OnNpcPlacementRequest);
            Register<NpcPlacementAnswerMessage>(MessageIds.NpcPlacementAnswer,
                (peer, m) => QueueFromHost(m, _incomingNpcPlacements, MaxPendingEnemyDeaths, "placement answers"));
            Register<DaytimeMessage>(MessageIds.Daytime, OnDaytime);
            Register<MapUnlockMessage>(MessageIds.MapUnlock,
                (peer, m) => RelayAndQueue(peer, m, _incomingMapUnlocks, MaxPendingEnemyDeaths, "map unlocks"));

            // Dialogue rewards never go back to the sender: a payment is not idempotent, and the sender's
            // own dialogue already paid it and filled its hands.
            Register<DialogueExpMessage>(MessageIds.DialogueExp,
                (peer, m) => RelayAndQueue(peer, m, _incomingDialogueExp, MaxPendingEnemyDeaths, "dialogue rewards"));
            Register<DialogueItemsMessage>(MessageIds.DialogueItems,
                (peer, m) => RelayAndQueue(peer, m, _incomingDialogueItems, MaxPendingEnemyDeaths, "dialogue items"));
            Register<EnemySnapshotMessage>(MessageIds.EnemySnapshot, OnEnemySnapshot);
            Register<WorldStateRequestMessage>(MessageIds.WorldStateRequest, OnWorldStateRequest);
            Register<EnemySpawnMessage>(MessageIds.EnemySpawn,
                (peer, m) => QueueFromHost(m, _incomingSpawns, MaxPendingEnemyDeaths, "spawned creatures"));
            Register<EnemyDriveMessage>(MessageIds.EnemyDrive,
                (peer, m) => QueueFromHost(m, _incomingDrives, MaxPendingEnemyDeaths, "creature drive changes"));
            Register<LootTakenMessage>(MessageIds.LootTaken, OnLootTaken);
            Register<LootSnapshotMessage>(MessageIds.LootSnapshot,
                (peer, m) => QueueFromHost(m, _incomingLootSnapshot, MaxPendingLootSnapshots, "loot the session had already seen go"));
            Register<LootSnapshotEndMessage>(MessageIds.LootSnapshotEnd, OnLootSnapshotEnd);
            Register<PlayerVitalsMessage>(MessageIds.PlayerVitals, OnPlayerVitals);
            Register<WorldObjectMessage>(MessageIds.WorldObject, OnWorldObject);
            Register<WorldObjectSnapshotMessage>(MessageIds.WorldObjectSnapshot, OnWorldObjectSnapshot);
            Register<WorldObjectSnapshotEndMessage>(MessageIds.WorldObjectSnapshotEnd, OnWorldObjectSnapshotEnd);
            Register<BlockVerdictMessage>(MessageIds.BlockVerdict, OnBlockVerdict);
            Register<ArenaStateMessage>(MessageIds.ArenaState, OnArenaState);
            Register<ArenaRoundMessage>(MessageIds.ArenaRound, OnArenaRound);
            Register<CacheSpillMessage>(MessageIds.CacheSpill, OnCacheSpill);
            Register<GameOverMessage>(MessageIds.GameOver, (peer, m) => { if (IsFromHost()) World.GameOver.Take(m.Checkpoint); });
            Register<CheckpointMessage>(MessageIds.Checkpoint, (peer, m) => World.Checkpoint.Take(peer.Id, IsFromHost(), m));
            Register<TradeMessage>(MessageIds.Trade, OnTrade);
            Register<ArenaTeamMessage>(MessageIds.ArenaTeam, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting || IsFromHost()) World.Arena.TakeTeam(m, IsFromHost());
            });
            Register<InviteMessage>(MessageIds.Invite, OnInvite);
            Register<StatusEndedMessage>(MessageIds.StatusEnded, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting)
                {
                    if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
                    m.Player = sender;
                    SendToClientsExcept(m, peer.Id);
                }
                else if (!IsFromHost()) return;

                World.StatusWatch.TakeEnded(m);
            });
            Register<DialogueLineMessage>(MessageIds.DialogueLine, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting)
                {
                    if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
                    m.Player = sender;
                    SendToClientsExcept(m, peer.Id);
                }
                else if (!IsFromHost()) return;

                World.SpokenLines.Show(m);
            });
            Register<ArenaGateMessage>(MessageIds.ArenaGate, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting) SendToClientsExcept(m, peer.Id);
                else if (!IsFromHost()) return;
                World.ArenaGates.Take(m);
            });
            Register<RegionMessage>(MessageIds.Region, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting && _peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) World.RegionShare.Take(sender, m);
            });
            Register<LeaseSummonMessage>(MessageIds.LeaseSummon, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting && _peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) World.LeaseSummons.Take(sender, m);
            });
            Register<TrapMessage>(MessageIds.Trap, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting) SendToClientsExcept(m, peer.Id);
                else if (!IsFromHost()) return;
                World.TrapShare.Take(m);
            });
            Register<FogletWakeMessage>(MessageIds.FogletWake, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting) SendToClientsExcept(m, peer.Id);
                else if (!IsFromHost()) return;
                World.FogletShare.Take(m);
            });

            NetworkEvents.OnHostStarted += OnHostStarted;
            NetworkEvents.OnConnectedToHost += OnConnectedToHost;
            NetworkEvents.OnPeerConnected += OnPeerConnected;
            NetworkEvents.OnPeerDisconnected += OnPeerDisconnected;
            NetworkEvents.OnConnectionRejected += OnConnectionRejected;
            NetworkEvents.OnNetworkError += OnNetworkError;

            _initialized = true;
        }

        /// <summary>
        /// Registers a message handler and records the matching unregister for <see cref="Shutdown"/>.
        /// </summary>
        private static void Register<T>(ushort id, Action<INetPeer, T> handler) where T : struct, INetMessage
        {
            NetworkAccess.RegisterWithId(id, handler);
            _unregisters.Add(NetworkAccess.Unregister<T>);
        }

        /// <summary>
        /// A message both sides may originate. The host queues a client's and passes it on to everyone
        /// but the sender. A client queues only what the host sent.
        /// </summary>
        private static void RelayAndQueue<T>(INetPeer peer, T message, Queue<T> queue, int cap, string what)
            where T : struct, INetMessage
        {
            if (State == CoopSessionState.Hosting)
            {
                Enqueue(queue, message, cap, what);
                SendToClientsExcept(message, peer.Id);
                return;
            }

            QueueFromHost(message, queue, cap, what);
        }

        /// <summary>
        /// A message only the host may send. Queued on a client when it came from there.
        /// </summary>
        private static void QueueFromHost<T>(T message, Queue<T> queue, int cap, string what) where T : struct
        {
            if (!IsFromHost()) return;

            Enqueue(queue, message, cap, what);
        }

        /// <summary>
        /// Hosts through a relay. Passing no code lets the relay pick one, which is the better option: a
        /// code the relay generates cannot be claimed by someone who guesses it first.
        /// </summary>
        /// <param name="relayAddress">The relay's hostname or address.</param>
        /// <param name="relayPort">The relay's UDP port.</param>
        /// <param name="sessionCode">A chosen code, or null to be given one.</param>
        /// <param name="password">The session password. Also the encryption key material, so a session
        /// without one is readable by the relay.</param>
        public static void HostViaRelay(string relayAddress, int relayPort, string? sessionCode, string? password)
        {
            if (!Begin()) return;

            MelonLogger.Msg($"[Coop] Hosting through {relayAddress}:{relayPort}...");
            NetworkAccess.StartHostViaRelay(relayAddress, relayPort, sessionCode, password);
        }

        /// <summary>
        /// Joins a relay session by its code.
        /// </summary>
        public static void JoinViaRelay(string relayAddress, int relayPort, string sessionCode, string? password)
        {
            if (!Begin()) return;

            MelonLogger.Msg($"[Coop] Joining '{sessionCode}' through {relayAddress}:{relayPort}...");
            NetworkAccess.ConnectViaRelay(relayAddress, relayPort, sessionCode, password);
        }

        /// <summary>
        /// Leaves whatever session is running and returns to <see cref="CoopSessionState.Idle"/>, clearing
        /// any earlier failure. To end a session and leave a reason on screen, use the failure paths
        /// instead - this one deliberately forgets why the last attempt went wrong.
        /// </summary>
        public static void Leave()
        {
            Teardown(announce: true);

            LastError = null;
            State = CoopSessionState.Idle;
        }

        /// <summary>
        /// Advances the session's own timers. Call once per frame while the mod is loaded.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame, unaffected by
        /// <c>Time.timeScale</c>. Scaled time would be wrong here: the transport does not stop because a
        /// player opened a menu, so a deadline measured in scaled time can freeze along with the world and
        /// never expire.</param>
        public static void Tick(float unscaledDeltaTime)
        {
            if (IsLive)
            {
                // The transport is gone under a live session: the relay dropped it (both ends lost their peer in the same
                // millisecond in one Red Tower run). The host saw its teammate leave and went on "hosting" a dead
                // connection - every send a no-op, no error, no way back in. Ended here, with a reason a player can read.
                if (NetworkAccess.Role == NetRole.None)
                {
                    Fail("The connection to the relay was lost. Host again to continue; teammates join with the new code.");
                    return;
                }

                TickRules();
                TickPing(unscaledDeltaTime);
                return;
            }

            if (State != CoopSessionState.Connecting) return;

            _connectingSeconds += unscaledDeltaTime;
            if (_connectingSeconds < ConnectTimeoutSeconds) return;

            Fail($"No answer from the host within {ConnectTimeoutSeconds:0} seconds. It may be running a different mod set, or none.");
        }

        /// <summary>
        /// The round trip to another player in milliseconds, or null before one has been measured.
        /// </summary>
        public static float? RoundTripMs(PlayerId player)
        {
            return _roundTripMs.TryGetValue(player, out float value) ? value : null;
        }

        /// <summary>
        /// Sends a ping when one is due and forgets the ones that will never be answered.
        /// </summary>
        private static void TickPing(float unscaledDeltaTime)
        {
            _sincePing += unscaledDeltaTime;
            if (_sincePing < PingIntervalSeconds) return;

            _sincePing = 0f;

            if (!_clock.IsRunning)
            {
                _clock.Restart();
            }

            // Dropped rather than left to accumulate: these go out unreliable, so an unanswered token is
            // ordinary rather than exceptional, and every one of them would otherwise stay in the table
            // for the length of the session.
            double now = _clock.Elapsed.TotalMilliseconds;

            foreach (KeyValuePair<uint, double> pending in _pingSentAt)
            {
                if (now - pending.Value > PingExpirySeconds * 1000d)
                {
                    _pingSentAt.Remove(pending.Key);
                }
            }

            uint token = unchecked(++_nextPingToken);
            _pingSentAt[token] = now;

            PingMessage ping = new()
            {
                Sender = LocalPlayerId,
                Token = token,
                IsReply = false,
            };

            NetworkAccess.SendToAll(ping, Delivery.Unreliable);
        }

        private static void OnPing(INetPeer peer, PingMessage message)
        {
            if (!IsLive) return;

            if (!message.IsReply)
            {
                // Echoed straight back with this side's identity, so the original sender can attribute the
                // measurement. The token is not interpreted here - it belongs to whoever sent it.
                PingMessage reply = new()
                {
                    Sender = LocalPlayerId,
                    Token = message.Token,
                    IsReply = true,
                };

                NetworkAccess.Send(peer, reply, Delivery.Unreliable);
                return;
            }

            if (!_pingSentAt.Remove(message.Token, out double sentAt)) return;

            // Who answered is the connection it came over, not the id the reply carries. A client can
            // only be answered by the host; the host answers by whichever player it handed that peer.
            PlayerId sender;
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out sender)) return;
            }
            else
            {
                sender = PlayerId.Host;
            }

            _roundTripMs[sender] = (float)(_clock.Elapsed.TotalMilliseconds - sentAt);
        }

        /// <summary>
        /// Drops every transport subscription and message registration. Call when the melon unloads:
        /// <see cref="NetworkEvents"/> and the API's message dispatcher are both static, so a hot reload
        /// otherwise leaves the previous load's handlers attached and every message is handled twice.
        /// </summary>
        public static void Shutdown()
        {
            Leave();

            if (!_initialized) return;

            NetworkEvents.OnHostStarted -= OnHostStarted;
            NetworkEvents.OnConnectedToHost -= OnConnectedToHost;
            NetworkEvents.OnPeerConnected -= OnPeerConnected;
            NetworkEvents.OnPeerDisconnected -= OnPeerDisconnected;
            NetworkEvents.OnConnectionRejected -= OnConnectionRejected;
            NetworkEvents.OnNetworkError -= OnNetworkError;

            foreach (Action unregister in _unregisters)
            {
                unregister();
            }

            _unregisters.Clear();

            _initialized = false;
        }

        /// <summary>
        /// Sends the local player's state to everyone. Unreliable by design: the next one supersedes it.
        ///
        /// Sequenced, not merely unreliable, for the reason the creature snapshots are: the far side keeps the
        /// newest state it was handed, so a datagram overtaken on the way - one sent before the one that
        /// arrived first - put its stale stance back on the copy. A player who crouched and stood up once was
        /// seen crouching twice, and a weapon drawn once was drawn, sheathed and drawn again (combat.coop).
        /// </summary>
        /// <param name="message">The state to send. The caller fills in the intent; this stamps the id,
        /// so no caller can send state claiming to be somebody else even by accident.</param>
        public static void BroadcastPlayerState(PlayerStateMessage message)
        {
            if (!IsLive) return;

            message.PlayerId = LocalPlayerId;
            NetworkAccess.SendToAll(message, Delivery.Sequenced);
        }

        /// <summary>
        /// Sends what the local player has equipped to everyone.
        ///
        /// Reliable, unlike player state: this is sent when it changes rather than continuously, so a lost
        /// one is not superseded a thirtieth of a second later - it is simply wrong until the player next
        /// changes their gear, which could be an hour.
        /// </summary>
        /// <param name="itemIds">The readable ids of everything equipped, in both sets. The caller gathers
        /// them; this stamps the sender, so no caller can dress somebody else even by accident.</param>
        /// <param name="itemSets">Which weapon set each item belongs to, one entry per item.</param>
        /// <param name="activeSet">Which weapon set is in hand.</param>
        /// <param name="bodyType">The local player's build.</param>
        /// <param name="voiceId">The local player's voice.</param>
        public static void BroadcastEquipment(string[] itemIds, byte[] itemSets, byte activeSet, byte bodyType, byte voiceId)
        {
            if (!IsLive) return;

            PlayerEquipmentMessage message = new()
            {
                PlayerId = LocalPlayerId,
                ItemIds = itemIds,
                ItemSets = itemSets,
                ActiveSet = activeSet,
                BodyType = bodyType,
                VoiceId = voiceId,
            };

            foreach (PlayerEquipmentMessage part in message.Split()) NetworkAccess.SendToAll(part);
        }

        private static void OnEquipment(INetPeer peer, PlayerEquipmentMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                // Relayed part by part, as it came: every receiver gathers the parts itself.
                message.PlayerId = sender;
                SendToClientsExcept(message, peer.Id);

                if (Gather(sender, ref message)) Remember(sender, message);
                return;
            }

            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            if (Gather(message.PlayerId, ref message)) Remember(message.PlayerId, message);
        }

        /// <summary>Tells everyone which talents the local player has learned. Stamps the sender.</summary>
        public static void BroadcastTalents(string[] guids)
        {
            if (!IsLive) return;

            foreach (PlayerTalentsMessage part in new PlayerTalentsMessage { PlayerId = LocalPlayerId, Guids = guids }.Split())
            {
                NetworkAccess.SendToAll(part);
            }
        }

        private static void OnTalents(INetPeer peer, PlayerTalentsMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                // Relayed part by part, as it came: every receiver gathers the parts itself.
                message.PlayerId = sender;
                SendToClientsExcept(message, peer.Id);

                if (Gather(sender, ref message)) RememberTalents(sender, message.Guids);
                return;
            }

            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            if (Gather(message.PlayerId, ref message)) RememberTalents(message.PlayerId, message.Guids);
        }

        /// <summary>
        /// Lists that crossed in parts, held until the part that says it was the last. Every receiver
        /// replaces what it knew with what arrives - a body is dressed in it, a chest is set to it - so
        /// acting on each part would leave only the last one standing.
        /// </summary>
        private static readonly Dictionary<PlayerId, PlayerEquipmentMessage> _equipmentParts = new();
        private static readonly Dictionary<PlayerId, string[]> _talentParts = new();
        private static readonly Dictionary<(int Peer, string Key), LootStack[]> _lootParts = new();

        /// <summary>
        /// The longest list parts are gathered into. The per-message caps used to bound what a peer could
        /// make this machine hold; this is that bound for a list in parts. Past it the parts are dropped.
        /// </summary>
        private const int MaxGathered = 255;

        /// <returns>True when the message is now the whole outfit, false while parts are still to come.</returns>
        private static bool Gather(PlayerId player, ref PlayerEquipmentMessage message)
        {
            if (_equipmentParts.Remove(player, out PlayerEquipmentMessage earlier))
            {
                message.ItemIds = [.. earlier.ItemIds, .. message.ItemIds];
                message.ItemSets = [.. earlier.ItemSets, .. message.ItemSets];
            }

            if (!message.More) return true;

            if (message.ItemIds.Length <= MaxGathered) _equipmentParts[player] = message;
            return false;
        }

        /// <returns>True when the message is now the whole list, false while parts are still to come.</returns>
        private static bool Gather(PlayerId player, ref PlayerTalentsMessage message)
        {
            if (_talentParts.Remove(player, out string[]? earlier)) message.Guids = [.. earlier, .. message.Guids];

            if (!message.More) return true;

            if (message.Guids.Length <= MaxGathered) _talentParts[player] = message.Guids;
            return false;
        }

        /// <returns>True when the message is now the whole container, false while parts are still to come.</returns>
        private static bool Gather(INetPeer peer, ref LootTakenMessage message)
        {
            (int, string) key = (peer.Id, message.RootKey);
            if (_lootParts.Remove(key, out LootStack[]? earlier)) message.Remaining = [.. earlier, .. message.Remaining];

            if (!message.More) return true;

            if (message.Remaining.Length <= MaxGathered) _lootParts[key] = message.Remaining;
            return false;
        }

        private static void RememberTalents(PlayerId player, string[] guids)
        {
            int version = _talents.TryGetValue(player, out (string[] Guids, int Version) existing) ? existing.Version + 1 : 1;
            _talents[player] = (guids, version);
        }

        /// <summary>
        /// Stores one player's appearance and counts the change, so a body knows it has something new to
        /// put on rather than having to compare lists itself.
        /// </summary>
        private static void Remember(PlayerId player, in PlayerEquipmentMessage message)
        {
            int version = _equipment.TryGetValue(player, out EquipmentSnapshot existing) ? existing.Version + 1 : 1;
            _equipment[player] = new EquipmentSnapshot(
                message.ItemIds ?? [],
                message.ItemSets ?? [],
                message.ActiveSet,
                message.BodyType,
                message.VoiceId,
                version);
        }

        /// <summary>
        /// Tells everyone that the local player started or stopped using an ability.
        ///
        /// Reliable, for the same reason equipment is: this goes out on the edges of a cast rather than
        /// continuously, so a lost start is not corrected a thirtieth of a second later - it is a spell
        /// the other players never see, and a lost release is a body left holding a cast it will never
        /// finish.
        /// </summary>
        /// <param name="abilityId">The readable id of the ability item. The caller reads it; this stamps
        /// the sender, so no caller can cast as somebody else even by accident.</param>
        /// <param name="sequence">Which cast this is, as the caller counted it.</param>
        /// <param name="released">Whether this is the end of the cast rather than the start.</param>
        public static void BroadcastItemUse(string abilityId, byte sequence, bool released)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(abilityId)) return;

            ItemUseMessage message = new()
            {
                User = LocalPlayerId,
                ItemId = abilityId,
                Sequence = sequence,
                Released = released ? (byte)1 : (byte)0,
            };

            NetworkAccess.SendToAll(message);
        }

        private static void OnItemUse(INetPeer peer, ItemUseMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.User = sender;
                Remember(sender, message);

                SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!IsFromHost()) return;
            if (message.User == LocalPlayerId) return;

            Remember(message.User, message);
        }

        /// <summary>
        /// Stores one player's latest cast and counts the announcement, so a body knows there is
        /// something new to perform rather than having to work that out from the ability id - which
        /// cannot answer it, because casting the same spell twice is two casts.
        /// </summary>
        private static void Remember(PlayerId player, in ItemUseMessage message)
        {
            int version = _itemUses.TryGetValue(player, out ItemUseSnapshot existing) ? existing.Version + 1 : 1;
            _itemUses[player] = new ItemUseSnapshot(message.ItemId ?? string.Empty, message.Sequence, message.Released != 0, version);
        }

        /// <summary>
        /// Tells another player that the local player just hit them, and how hard.
        ///
        /// The figure is the blow before their defense rather than the wound after it - see
        /// <see cref="DamageReportMessage"/> for why the victim's own game has to be the one that resists
        /// it. Nothing is applied here; a session layer that reached into a health component would be the
        /// one place in this mod where the wire and the game meet, and that is what
        /// <see cref="TryTakeDamage"/> exists to avoid.
        /// </summary>
        /// <param name="victim">Whose body was struck.</param>
        /// <param name="amount">How hard, as a positive number. Zero and below are ignored, so a caller
        /// watching the game's damage funnel can hand over whatever it read without sorting healing out
        /// twice.</param>
        /// <param name="poiseDamage">How much stance the blow was worth, as a positive figure. Anything
        /// else is sent as none.</param>
        /// <param name="appliesBleed">Whether the blow was the kind that opens a wound. The size of the
        /// wound is left to the victim, who is the only one who knows what actually landed.</param>
        /// <param name="damageType">Which resistance answers it. See
        /// <see cref="DamageReportMessage.DamageType"/>.</param>
        /// <param name="origin">Whether a player or one of the world's creatures struck the blow. It is
        /// what the friendly-fire rule is applied to: a rule about whether teammates may hurt each other
        /// has nothing to say about a wolf, and applying it to one silently ate every blow a creature
        /// landed on a remote player.</param>
        public static void ReportDamage(PlayerId victim, int amount, float poiseDamage, bool appliesBleed,
            byte damageType, byte origin = DamageReportMessage.OriginPlayer)
        {
            if (!IsLive) return;

            // Checked here as well as on arrival. A blow struck in the frame the host turned the rule off
            // should not land, and this is the earlier of the two chances to say so.
            //
            // Only for blows between players, which is the whole of what the rule is about. A creature the
            // host drives cannot swing on its victim's machine, so this message is the only way its blows
            // ever arrive - refusing them would make friendly fire off mean invulnerable to the world.
            if (origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(victim)) return;

            if (!victim.IsAssigned || victim == LocalPlayerId) return;
            if (amount <= 0) return;

            // Capped rather than wrapped: a number that overflowed into a scratch would be a fight nobody
            // could explain, and one that big is a bug on this side either way.
            if (amount > ushort.MaxValue)
            {
                amount = ushort.MaxValue;
            }

            DamageReportMessage report = new()
            {
                Attacker = LocalPlayerId,
                Victim = victim,
                Amount = (ushort)amount,
                Sequence = unchecked(++_nextDamageSequence),

                // Anything that is not a real, positive figure is sent as none. A blow carries poise or it
                // does not, and a not-a-number reaching the far side would be arithmetic on their stance
                // rather than a stagger.
                PoiseDamage = poiseDamage > 0f && !float.IsNaN(poiseDamage) && !float.IsInfinity(poiseDamage) ? poiseDamage : 0f,
                AppliesBleed = appliesBleed ? (byte)1 : (byte)0,
                DamageType = damageType,
                Origin = origin,
            };

            // Remembered before it goes out, so the verdict that answers it is recognisable when it comes
            // back. Only a player's blow is ever answered.
            if (origin == DamageReportMessage.OriginPlayer) World.BlockVerdicts.Await(victim, report.Sequence);

            if (NetworkAccess.Role == NetRole.Host)
            {
                TrySendToPlayer(report, victim);
                return;
            }

            // A client has exactly one peer - the host - so this reaches it and nobody else. Addressing the
            // victim directly is not an option even in a relay session: peer ids are machine-local, so this
            // side cannot name the peer the victim is, and the host can.
            NetworkAccess.SendToAll(report);
        }

        /// <summary>
        /// Takes the next blow that has arrived for the local player, oldest first.
        ///
        /// Queued rather than applied where it lands, because a report can arrive before there is a body to
        /// apply it to - between joining and the world coming up, or across a scene change - and because
        /// the game may only be touched from the frame loop. Drain it until it returns false.
        /// </summary>
        /// <param name="report">The blow, when there is one.</param>
        /// <returns>Whether anything was waiting.</returns>
        public static bool TryTakeDamage(out DamageReportMessage report)
            => _incomingDamage.TryDequeue(out report);

        private static void OnDamageReport(INetPeer peer, DamageReportMessage message)
        {
            if (!IsLive) return;

            // The host's rule, applied by the machine that will take the damage. A peer running a build
            // that never asked is refused here rather than trusted to have checked. A creature's blow is
            // not what the rule is about - see DamageReportMessage.Origin.
            if (message.Amount == 0) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                // The sender does not get to say who swung. Without this a client could report damage in
                // somebody else's name, which is worse than lying about its own: the victim would see a
                // fight with a player who never touched them.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Attacker = sender;

                // Asked about the pair that is actually fighting, once the attacker is known for certain.
                if (message.Origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(sender, message.Victim)) return;

                // Hitting yourself is not a thing the wire needs to carry - a player's own swings land on
                // their own machine - so a report claiming it is either a bug or an attempt to spend
                // somebody else's health twice.
                if (message.Victim == sender) return;

                if (message.Victim == LocalPlayerId)
                {
                    Enqueue(message);
                    return;
                }

                if (!_players.ContainsKey(message.Victim)) return;

                TrySendToPlayer(message, message.Victim);
                return;
            }

            // A client takes the host's word for who swung, because the host is the authority on identity,
            // and only for blows addressed to it. The host does not send any others, so anything else here
            // came from a peer talking out of turn in a relay session.
            if (!IsFromHost()) return;
            if (message.Victim != LocalPlayerId) return;
            if (message.Origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(message.Attacker)) return;

            Enqueue(message);
        }

        /// <summary>
        /// Records a blow for the local player, once.
        /// </summary>
        private static void Enqueue(in DamageReportMessage report)
        {
            // The high-water mark per attacker, which is the whole defense against a report being applied
            // twice. Reliable delivery makes a duplicate unlikely rather than impossible, and the transport
            // deliberately does not reject a replayed packet.
            if (_lastDamageSequence.TryGetValue(report.Attacker, out uint last) && report.Sequence <= last) return;

            _lastDamageSequence[report.Attacker] = report.Sequence;

            EnqueueLatest(_incomingDamage, report, MaxPendingDamage);
        }

        /// <summary>
        /// Tells everyone that the local player learned something they should all know.
        ///
        /// Reaches everybody, in both directions, which nothing else here does. A recipe is not
        /// something one player did to another; it is a fact about the world that happens to have been
        /// discovered by whoever got to the paper first, and the paper is gone afterwards.
        /// </summary>
        /// <param name="kind">Which sort of thing was learned.</param>
        /// <param name="id">What was learned, in whatever form that kind is named by.</param>
        public static void BroadcastKnowledge(byte kind, string id)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(id)) return;

            KnowledgeMessage message = new()
            {
                Learner = LocalPlayerId,
                Kind = kind,
                Id = id,
            };

            NetworkAccess.SendToAll(message);
        }

        /// <summary>
        /// Takes the next thing learned elsewhere that this player has not been taught yet.
        /// </summary>
        /// <param name="message">What was learned, when there is something.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeKnowledge(out KnowledgeMessage message)
            => _incomingKnowledge.TryDequeue(out message);

        private static void OnKnowledge(INetPeer peer, KnowledgeMessage message)
        {
            if (!IsLive) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Learner = sender;

                // Passed on before it is applied here, so a third player learns it at the same time the
                // host does rather than waiting for the host's world to be ready to teach it.
                SendToClientsExcept(message, peer.Id);
            }
            else if (!IsFromHost() || message.Learner == LocalPlayerId)
            {
                return;
            }

            // The oldest is kept, not dropped, which is the opposite of the damage queue. Nothing repeats
            // a recipe: the paper it came on was consumed on somebody else's machine. The cap only stops a
            // hostile peer growing this without bound, so reaching it is worth a line.
            if (!Enqueue(_incomingKnowledge, message, MaxPendingKnowledge))
            {
                MelonLogger.Warning($"[Know] more than {MaxPendingKnowledge} things are waiting to be learned; " +
                    $"'{message.Id}' from {message.Learner} was refused and is lost.");
            }
        }

        /// <summary>
        /// Tells everyone what a creature was worth, so they can be paid for it too.
        ///
        /// Reaches everybody, in both directions, for the same reason knowledge does: there is no
        /// attacker and no victim, only a machine that awarded something and machines that have not.
        /// </summary>
        /// <param name="netId">Which creature, by the session's handle for it.</param>
        /// <param name="amount">What it was worth on this machine.</param>
        public static void BroadcastExperience(EnemyNetId netId, int amount)
        {
            if (!IsLive) return;
            if (netId == EnemyNetId.None || amount <= 0) return;

            ExperienceMessage message = new()
            {
                Earner = LocalPlayerId,
                NetId = netId,
                Amount = amount,
            };

            NetworkAccess.SendToAll(message);
        }

        /// <summary>
        /// Takes the next creature somebody else was paid for that this player has not been.
        /// </summary>
        /// <param name="message">What it was worth, when there is something.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeExperience(out ExperienceMessage message)
            => _incomingExperience.TryDequeue(out message);

        private static void OnExperience(INetPeer peer, ExperienceMessage message)
        {
            if (!IsLive) return;
            if (!message.IsPlausible()) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Earner = sender;

                SendToClientsExcept(message, peer.Id);
            }
            else if (!IsFromHost() || message.Earner == LocalPlayerId)
            {
                return;
            }

            // Refused rather than dropping the oldest, like knowledge: nothing restates a payout.
            if (!Enqueue(_incomingExperience, message, MaxPendingExperience))
            {
                MelonLogger.Warning($"[Exp] more than {MaxPendingExperience} kills are waiting to be paid out; " +
                    $"{message.NetId} from {message.Earner} was refused and that experience is lost.");
            }
        }

        /// <summary>
        /// Tells everyone that something which is not a player died here.
        ///
        /// Reaches everybody, in both directions, like knowledge and for a related reason: a dead
        /// creature is a fact about the world rather than something one player did to another, and which
        /// machine happened to land the blow is not what the message is about.
        ///
        /// Refused when this creature has already been reported or applied. That is what keeps the relay
        /// from echoing - a client's death goes to the host, the host passes it to everyone else, and with
        /// three players it comes back to a machine that has already seen it.
        /// </summary>
        /// <param name="actorGuid">The guid of the creature that died.</param>
        /// <param name="isReconcile">Whether this is a corpse being restored from a save rather than a
        /// kill that just happened.</param>
        public static void ReportEnemyDeath(string actorGuid, bool isReconcile)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(actorGuid)) return;

            if (!_deadEnemies.Add(actorGuid)) return;

            NetworkAccess.SendToAll(new EnemyDeathMessage
            {
                ActorGuid = actorGuid,
                Flags = isReconcile ? EnemyDeathMessage.FlagReconcile : (byte)0,
            });
        }

        /// <summary>
        /// Takes the next death that has arrived and not been applied yet.
        /// </summary>
        /// <param name="message">What died, when there is something.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeEnemyDeath(out EnemyDeathMessage message)
            => _incomingEnemyDeaths.TryDequeue(out message);

        /// <summary>How many creatures this session knows to be dead. For <c>coop_status</c>.</summary>
        public static int KnownDeadEnemies => _deadEnemies.Count;

        /// <summary>
        /// Takes a creature out of the session's dead set, so later asks about it are answered from the
        /// host's decree instead. The revive command's half of the answer. The other half is
        /// <see cref="World.EnemyRegistry.ForceAlive"/>.
        /// </summary>
        /// <param name="actorGuid">Which creature.</param>
        public static void ForgetEnemyDeath(string actorGuid)
        {
            if (string.IsNullOrEmpty(actorGuid)) return;

            _deadEnemies.Remove(actorGuid);
        }

        private static void OnEnemyDeath(INetPeer peer, EnemyDeathMessage message)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(message.ActorGuid)) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                // Refused before it is passed on, not after. A guid already known is one every other peer
                // was told about when it first arrived, so relaying it again would put the same death
                // around the session once per report.
                if (!_deadEnemies.Add(message.ActorGuid)) return;

                // Passed on before it is applied here, so a third player learns of it at the same time the
                // host does rather than waiting for the host's world to have a body to kill.
                SendToClientsExcept(message, peer.Id);
            }
            else if (!IsFromHost() || !_deadEnemies.Add(message.ActorGuid))
            {
                return;
            }

            // Refused rather than dropping the oldest, like knowledge. Nothing repeats a death: the
            // creature is already gone on the machine that sent it.
            if (!Enqueue(_incomingEnemyDeaths, message, MaxPendingEnemyDeaths))
            {
                MelonLogger.Warning($"[Enemy] more than {MaxPendingEnemyDeaths} deaths are waiting to be applied; " +
                    $"'{message.ActorGuid}' was refused and that creature will stay alive here.");
            }
        }

        /// <summary>
        /// Claims a creature and tells every client about it.
        /// </summary>
        /// <param name="id">The handle it will be known by.</param>
        /// <param name="actorGuid">Which creature.</param>
        /// <param name="health">Its health right now.</param>
        /// <param name="maxHealth">Its maximum right now.</param>
        /// <param name="isDead">Whether it is already dead here.</param>
        /// <param name="hostDrives">Whether the host also drives its body rather than only owning its
        /// health.</param>
        public static void BindEnemy(EnemyNetId id, string actorGuid, ushort health, ushort maxHealth, bool isDead, bool hostDrives)
        {
            if (State != CoopSessionState.Hosting) return;

            byte flags = isDead ? EnemyBindMessage.FlagDeadAtBind : (byte)0;

            if (hostDrives)
            {
                flags |= EnemyBindMessage.FlagHostDrives;
            }

            NetworkAccess.SendToAll(new EnemyBindMessage
            {
                NetId = id,
                ActorGuid = actorGuid,
                Health = health,
                MaxHealth = maxHealth,
                Flags = flags,
            });
        }

        /// <summary>
        /// Tells everyone which handle one conjured creature answers to.
        /// </summary>
        /// <param name="caster">Whose spell it was.</param>
        /// <param name="castSequence">Which of that player's casts.</param>
        /// <param name="index">Which summon of that cast.</param>
        /// <param name="id">The handle the host gave its own copy.</param>
        public static void BroadcastSummonBind(PlayerId caster, byte castSequence, byte index, EnemyNetId id)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new SummonBindMessage
            {
                Caster = caster,
                CastSequence = castSequence,
                Index = index,
                NetId = id,
            });
        }

        /// <summary>Takes the next conjured creature the host has named.</summary>
        /// <param name="message">The pairing.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeSummonBind(out SummonBindMessage message)
            => _incomingSummonBinds.TryDequeue(out message);

        /// <summary>
        /// Tells every client whether the host moves one spawned creature's body.
        ///
        /// Reliable and ordered like the pairing messages, on the same channel, so a statement can
        /// never overtake the spawn or summon bind that gives its handle a body to mean.
        /// </summary>
        /// <param name="id">Which creature.</param>
        /// <param name="hostDrives">Whether the host moves it.</param>
        public static void SendEnemyDrive(EnemyNetId id, bool hostDrives)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyDriveMessage
            {
                NetId = id,
                HostDrives = hostDrives,
            });
        }

        /// <summary>
        /// Gives a creature back and tells every client.
        /// </summary>
        /// <param name="id">Which creature.</param>
        /// <param name="reason">Why, for the log at the far end.</param>
        public static void ReleaseEnemy(EnemyNetId id, byte reason)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyReleaseMessage
            {
                NetId = id,
                Reason = reason,
            });
        }

        /// <summary>
        /// Tells everyone what a creature's health is.
        /// </summary>
        /// <param name="id">Which creature.</param>
        /// <param name="health">Its health.</param>
        /// <param name="maxHealth">Its maximum.</param>
        public static void BroadcastEnemyHealth(EnemyNetId id, ushort health, ushort maxHealth)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyHealthMessage
            {
                NetId = id,
                Health = health,
                MaxHealth = maxHealth,
            });
        }

        /// <summary>
        /// Reports a blow the local player landed on a creature the host owns.
        ///
        /// Sequenced so a resend is harmless, exactly as player damage is.
        /// </summary>
        /// <param name="id">Which creature.</param>
        /// <param name="amount">What the blow was worth before the defender's side of it.</param>
        /// <param name="poiseDamage">The raw stance figure, resolved by the host.</param>
        /// <param name="appliesBleed">Whether the blow may open a wound.</param>
        /// <param name="damageType">Physical, magical or pure.</param>
        /// <param name="flags">Additional flags for the damage report.</param>
        public static void ReportEnemyDamage(EnemyNetId id, int amount, float poiseDamage, bool appliesBleed,
            byte damageType, byte flags = 0)
        {
            if (State != CoopSessionState.Joined) return;
            if (amount <= 0) return;

            NetworkAccess.SendToAll(BuildEnemyDamage(id, amount, poiseDamage, appliesBleed, damageType, flags));
        }

        /// <summary>
        /// A blow the local player landed on a creature, numbered from this machine's own sequence.
        /// </summary>
        private static EnemyDamageMessage BuildEnemyDamage(EnemyNetId id, int amount, float poiseDamage,
            bool appliesBleed, byte damageType, byte flags)
        {
            return new EnemyDamageMessage
            {
                Attacker = LocalPlayerId,
                NetId = id,
                Sequence = ++_nextEnemyDamageSequence,
                Amount = amount >= ushort.MaxValue ? ushort.MaxValue : (ushort)amount,
                PoiseDamage = poiseDamage,
                AppliesBleed = appliesBleed ? (byte)1 : (byte)0,
                DamageType = damageType,
                Flags = flags,
            };
        }

        /// <summary>
        /// Tells the host what this player is doing with one of its NPCs - a dialogue, a brawl, or that
        /// the engagement ended. Repeated by the caller while it lasts; the host expires the silent.
        /// </summary>
        /// <param name="id">Which NPC.</param>
        /// <param name="mode">One of the <see cref="EnemyEngageMessage"/> mode constants.</param>
        public static void ReportEnemyEngage(EnemyNetId id, byte mode)
        {
            if (State != CoopSessionState.Joined) return;

            NetworkAccess.SendToAll(new EnemyEngageMessage
            {
                Player = LocalPlayerId,
                NetId = id,
                Mode = mode,
            });
        }

        /// <summary>
        /// Sends everything already dead to somebody who has just joined, in chunks.
        /// </summary>
        /// <param name="peer">Who to tell.</param>
        /// <param name="dead">Everything the host knows to be dead.</param>
        private static void SendGraveyard(INetPeer peer, IReadOnlyCollection<string> dead)
        {
            if (State != CoopSessionState.Hosting || peer == null) return;

            // The last chunk always goes, even when empty: it is what tells the far side the list has
            // ended, and a client that never hears that is a client waiting for a message that will not
            // come. A short chunk is the last one.
            foreach (string[] chunk in dead.Chunk(EnemyGraveyardMessage.MaxGuids))
            {
                bool last = chunk.Length < EnemyGraveyardMessage.MaxGuids;

                NetworkAccess.Send(peer, new EnemyGraveyardMessage { Guids = chunk, More = last ? (byte)0 : (byte)1 });

                if (last) return;
            }

            NetworkAccess.Send(peer, new EnemyGraveyardMessage { Guids = [], More = 0 });
        }

        private static void SendGraveyardChunk(INetPeer peer, bool more)
        {
            NetworkAccess.Send(peer, new EnemyGraveyardMessage
            {
                Guids = [.. _graveyardChunk],
                More = more ? (byte)1 : (byte)0,
            });

            _graveyardChunk.Clear();
        }

        /// <summary>
        /// Sends where the creatures the host drives are.
        ///
        /// Not resent, like the player stream: every record is replaced within a fraction of a second, so
        /// a resend would arrive after the truth had moved on.
        ///
        /// Sequenced rather than merely unreliable, which costs nothing and settles a question the
        /// receiver cannot. The far side keeps only the newest batch it has been handed, so a datagram
        /// that arrives late - after one sent behind it - walks every creature in it back by one send
        /// interval before the next batch pulls them forward again. Sequenced delivery drops an
        /// out-of-order batch at the transport instead, which is what "keep the newest" already assumed.
        /// </summary>
        /// <param name="message">The batch.</param>
        public static void BroadcastEnemySnapshot(in EnemySnapshotMessage message)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(message, Delivery.Sequenced);
        }

        /// <summary>Takes the next batch of creature positions that has arrived.</summary>
        /// <param name="message">The batch.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeEnemySnapshot(out EnemySnapshotMessage message)
            => _incomingSnapshots.TryDequeue(out message);

        /// <summary>
        /// Sends the host this client's leased fighters - the NPCs of a scripted brawl it runs for real - so the host's
        /// held copies move with them (LeaseMirror). Sequenced, as the host's own batches are.
        /// </summary>
        public static void SendLeaseSnapshot(in EnemySnapshotMessage message)
        {
            if (State != CoopSessionState.Joined) return;

            NetworkAccess.SendToAll(message, Delivery.Sequenced);
        }

        /// <summary>Takes the newest batch a client sent about its leased fighters, and who sent it.</summary>
        public static bool TryTakeLeaseSnapshot(out PlayerId sender, out EnemySnapshotMessage message)
        {
            foreach (KeyValuePair<PlayerId, EnemySnapshotMessage> pending in _incomingLeaseSnapshots)
            {
                sender = pending.Key;
                message = pending.Value;
                _incomingLeaseSnapshots.Remove(sender);
                return true;
            }

            sender = default;
            message = default;
            return false;
        }

        // Newest per client: each batch supersedes that client's last.
        private static readonly Dictionary<PlayerId, EnemySnapshotMessage> _incomingLeaseSnapshots = new();

        private static void OnEnemySnapshot(INetPeer peer, EnemySnapshotMessage message)
        {
            // On the host, a client's leased fighters. Who sent it is the connection's word, not the message's.
            if (State == CoopSessionState.Hosting)
            {
                if (_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) _incomingLeaseSnapshots[sender] = message;
                return;
            }

            if (!IsFromHost()) return;

            // Only the newest. These arrive faster than the frame loop drains them and every one
            // supersedes the last, so a queue that grew would be a queue of stale positions applied in
            // order - a creature retracing where it has already been.
            //
            // Snapshots only. This used to clear the spawn queue as well, which is the opposite of what
            // that queue is for: a spawn announcement is said once and supersedes nothing, so anything
            // still waiting there was destroyed by the next position update - thirty times a second. A
            // conjured creature only survived if the frame loop happened to drain it before one arrived.
            _incomingSnapshots.Clear();
            _incomingSnapshots.Enqueue(message);
        }

        /// <summary>
        /// Asks the host which of these creatures its world has already killed.
        /// </summary>
        /// <param name="guids">The creatures in question.</param>
        public static void AskAboutWorldState(List<string> guids)
        {
            if (State != CoopSessionState.Joined || guids == null || guids.Count == 0) return;

            NetworkAccess.SendToAll(new WorldStateRequestMessage { Guids = guids.ToArray() });
        }

        private static void OnWorldStateRequest(INetPeer peer, WorldStateRequestMessage message)
        {
            if (State != CoopSessionState.Hosting || message.Guids == null) return;

            _graveyardChunk.Clear();
            _aliveChunk.Clear();

            foreach (string guid in message.Guids)
            {
                if (string.IsNullOrEmpty(guid)) continue;

                // A creature the host has bound alive or revived by decree. Checked before the save,
                // because a revived creature stays dead on disk until the next autosave rewrites it.
                if (World.EnemyRegistry.IsKnownAlive(guid))
                {
                    _aliveChunk.Add(guid);

                    if (_aliveChunk.Count >= EnemyAliveMessage.MaxGuids)
                    {
                        SendAliveChunk(peer);
                    }

                    continue;
                }

                // Answered from the host's own world, which means both what has died this session and what
                // its save file already recorded. The second is the half that matters here: a creature the
                // host killed weeks ago in a solo game is dead in its world and has never been mentioned
                // on the wire.
                if (_deadEnemies.Contains(guid) || !SavegameDataExtensions.IsActorAlive(guid))
                {
                    _graveyardChunk.Add(guid);

                    if (_graveyardChunk.Count >= EnemyGraveyardMessage.MaxGuids)
                    {
                        SendGraveyardChunk(peer, more: true);
                    }

                    continue;
                }

                // The other verdict, said out loud rather than implied. The client only ever asks about
                // creatures its own world has as corpses, so "alive" is always news to it - and it is the
                // verdict the asker cannot act on unless it is spoken, because a creature the host is
                // nowhere near will never get the bind that used to be the only way of saying this.
                _aliveChunk.Add(guid);

                if (_aliveChunk.Count >= EnemyAliveMessage.MaxGuids)
                {
                    SendAliveChunk(peer);
                }
            }

            // Always sent, even when nothing in the batch was dead. The client is waiting on an answer for
            // every guid it asked about, and silence is indistinguishable from a lost packet - it would
            // ask again forever.
            SendGraveyardChunk(peer, more: false);

            if (_aliveChunk.Count > 0)
            {
                SendAliveChunk(peer);
            }
        }

        private static void SendAliveChunk(INetPeer peer)
        {
            NetworkAccess.Send(peer, new EnemyAliveMessage
            {
                Guids = [.. _aliveChunk],
            });

            _aliveChunk.Clear();
        }

        /// <summary>
        /// Tells everyone about a creature this machine spawned at runtime.
        /// </summary>
        /// <param name="id">The handle it will be known by.</param>
        /// <param name="assetGuid">What it was built from.</param>
        /// <param name="position">Where it was put.</param>
        public static void BroadcastEnemySpawn(EnemyNetId id, string assetGuid, UnityEngine.Vector2 position)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemySpawnMessage
            {
                NetId = id,
                AssetGuid = assetGuid,
                X = position.x,
                Y = position.y,
            });
        }

        /// <summary>Takes the next spawn the host has announced.</summary>
        /// <param name="message">The spawn.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeEnemySpawn(out EnemySpawnMessage message)
            => _incomingSpawns.TryDequeue(out message);

        /// <summary>Takes the next claim that has arrived.</summary>
        /// <param name="message">The claim.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeEnemyBind(out EnemyBindMessage message)
            => _incomingBinds.TryDequeue(out message);

        /// <summary>Takes the next release that has arrived.</summary>
        /// <param name="message">The release.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeEnemyRelease(out EnemyReleaseMessage message)
            => _incomingReleases.TryDequeue(out message);

        /// <summary>Takes the next statement of who moves a spawned creature.</summary>
        /// <param name="message">The statement.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeEnemyDrive(out EnemyDriveMessage message)
            => _incomingDrives.TryDequeue(out message);

        /// <summary>Takes the next graveyard chunk that has arrived.</summary>
        /// <param name="message">The chunk.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeGraveyard(out EnemyGraveyardMessage message)
            => _incomingGraveyard.TryDequeue(out message);

        /// <summary>Takes the next chunk of creatures the host has said are alive.</summary>
        /// <param name="message">The chunk.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeAlive(out EnemyAliveMessage message)
            => _incomingAlive.TryDequeue(out message);

        /// <summary>Takes the next chunk of creatures the host has revived by decree.</summary>
        /// <param name="message">The chunk.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeRevive(out EnemyReviveMessage message)
            => _incomingRevive.TryDequeue(out message);

        /// <summary>Takes the next live global-variable change that arrived.</summary>
        /// <param name="message">The change.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeGVarChange(out GVarChangeMessage message)
            => _incomingGVars.TryDequeue(out message);

        /// <summary>Takes the next join catch-up record. Applied as state only, never as an event.</summary>
        /// <param name="message">The record, in live-change shape.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeGVarCatchup(out GVarChangeMessage message)
            => _incomingGVarCatchup.TryDequeue(out message);

        /// <summary>Takes the next shared cutscene start that arrived.</summary>
        /// <param name="message">The start.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeCutscene(out CutsceneMessage message)
            => _incomingCutscenes.TryDequeue(out message);

        /// <summary>Takes the next NPC port a teammate's cutscene made, if any.</summary>
        public static bool TryTakeActorPort(out ActorPortMessage message)
            => _incomingActorPorts.TryDequeue(out message);

        /// <summary>Takes the next weather report from the host.</summary>
        /// <param name="message">The report.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeWeather(out WeatherMessage message)
            => _incomingWeather.TryDequeue(out message);

        /// <summary>Takes the next batch of shared journal entries.</summary>
        /// <param name="message">The batch.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeJournal(out JournalEntryMessage message)
            => _incomingJournal.TryDequeue(out message);

        /// <summary>Takes the next placement answer from the host.</summary>
        /// <param name="message">The answer.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeNpcPlacement(out NpcPlacementAnswerMessage message)
            => _incomingNpcPlacements.TryDequeue(out message);

        /// <summary>
        /// Tells the session about a journal entry this player earned. On a client that reaches the
        /// host, which adds it and passes it on.
        /// </summary>
        /// <param name="path">The entry's resource path.</param>
        public static void ReportJournalEntry(string path)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(path)) return;

            NetworkAccess.SendToAll(new JournalEntryMessage { Paths = [path] });
        }

        /// <summary>Takes the next completion mark that arrived.</summary>
        /// <param name="message">The mark.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeJournalTopic(out JournalTopicMessage message)
            => _incomingJournalTopics.TryDequeue(out message);

        /// <summary>
        /// Tells the session that a quest's journal topic changed category here. On a client that
        /// reaches the host, which applies it and passes it on.
        /// </summary>
        /// <param name="message">The mark.</param>
        public static void ReportJournalTopic(in JournalTopicMessage message)
            => SendIfLive(message);

        /// <summary>
        /// Asks the host where its world has these actors. Client only; the answer comes back as an
        /// <see cref="NpcPlacementAnswerMessage"/>.
        /// </summary>
        /// <param name="message">The question.</param>
        public static void AskNpcPlacement(in NpcPlacementRequestMessage message)
        {
            if (State != CoopSessionState.Joined) return;

            NetworkAccess.SendToAll(message);
        }

        /// <summary>
        /// Tells every client what the host's sky is doing. Host only, because weather is one of the
        /// world's dice rolls and the host's world is the one everybody follows.
        /// </summary>
        /// <param name="message">The parameters.</param>
        public static void BroadcastWeather(in WeatherMessage message)
            => SendIfHost(message);

        /// <summary>Takes the next clock report from the host.</summary>
        /// <param name="message">The report.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeDaytime(out DaytimeMessage message)
            => _incomingDaytime.TryDequeue(out message);

        /// <summary>
        /// Tells every client what time it is. Host only, for the same reason the weather is.
        /// </summary>
        /// <param name="message">The clock.</param>
        public static void BroadcastDaytime(in DaytimeMessage message)
            => SendIfHost(message);

        /// <summary>Takes the next shared map unlock that arrived.</summary>
        /// <param name="message">The unlock.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeMapUnlock(out MapUnlockMessage message)
            => _incomingMapUnlocks.TryDequeue(out message);

        /// <summary>
        /// Tells the session that a map was unlocked here. On a client that reaches the host, which
        /// applies it and passes it on.
        /// </summary>
        /// <param name="guid">The map definition's guid.</param>
        public static void ReportMapUnlock(string guid)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(guid)) return;

            NetworkAccess.SendToAll(new MapUnlockMessage { Guid = guid });
        }

        /// <summary>Takes the next dialogue reward that arrived.</summary>
        /// <param name="message">The grant.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeDialogueExp(out DialogueExpMessage message)
            => _incomingDialogueExp.TryDequeue(out message);

        /// <summary>
        /// Tells the session that a dialogue paid this player. On a client that reaches the host,
        /// which pays and passes it on with the sender excluded.
        /// </summary>
        /// <param name="message">The grant.</param>
        public static void ReportDialogueExp(in DialogueExpMessage message)
            => SendIfLive(message);

        /// <summary>Takes the next dialogue item grant that arrived.</summary>
        /// <param name="message">The grant.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeDialogueItems(out DialogueItemsMessage message)
            => _incomingDialogueItems.TryDequeue(out message);

        /// <summary>
        /// Tells the session that a dialogue handed this player items. On a client that reaches the
        /// host, which grants and passes it on with the sender excluded.
        /// </summary>
        /// <param name="message">The grant.</param>
        public static void ReportDialogueItems(in DialogueItemsMessage message)
            => SendIfLive(message);

        /// <summary>
        /// Tells the session that a shared cutscene started here. On a client that reaches the host,
        /// which replays it and passes it on.
        /// </summary>
        /// <param name="message">The start.</param>
        public static void ReportCutscene(in CutsceneMessage message)
            => SendIfLive(message);

        /// <summary>Tells the session that a cutscene here ported an NPC. On a client that reaches the host, which passes it on.</summary>
        public static void ReportActorPort(in ActorPortMessage message)
            => SendIfLive(message);

        /// <summary>
        /// Tells the session about a global variable this machine changed. On a client that reaches the
        /// host, which applies it and passes it on.
        /// </summary>
        /// <param name="message">The change.</param>
        public static void ReportGVarChange(in GVarChangeMessage message)
            => SendIfLive(message);

        /// <summary>
        /// Tells every client that these creatures are alive again, whatever they were told before.
        /// The push half of the revive command. The answer half is <see cref="ForgetEnemyDeath"/> and
        /// the decree in the world-state answer.
        /// </summary>
        /// <param name="guids">The revived creatures.</param>
        public static void AnnounceEnemyRevive(List<string> guids)
        {
            if (State != CoopSessionState.Hosting || guids == null || guids.Count == 0) return;

            foreach (string[] chunk in guids.Chunk(EnemyReviveMessage.MaxGuids))
            {
                NetworkAccess.SendToAll(new EnemyReviveMessage { Guids = chunk });
            }
        }

        /// <summary>Takes the next authoritative health figure that has arrived.</summary>
        /// <param name="message">The figure.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeEnemyHealth(out EnemyHealthMessage message)
            => _incomingEnemyHealth.TryDequeue(out message);

        /// <summary>Takes the next blow a client reported. Host only.</summary>
        /// <param name="message">The blow.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeEnemyDamage(out EnemyDamageMessage message)
            => _incomingEnemyDamage.TryDequeue(out message);

        /// <summary>
        /// Asks the host to turn the world over, because this player just slept. Clients only.
        ///
        /// Nothing waits for an answer. The host decides for itself whether the ask is reasonable, and
        /// what comes back is the ordinary clock report with a new time in it - which this world then
        /// follows as a sleep, the same as it follows the host's own.
        /// </summary>
        /// <param name="targetTotalSeconds">The time this player was trying to wake up at.</param>
        /// <param name="scripted">Whether a dialogue set it rather than a bed.</param>
        public static void RequestDayAdvance(double targetTotalSeconds, bool scripted = false)
        {
            if (State != CoopSessionState.Joined) return;

            NetworkAccess.SendToAll(new DayAdvanceRequestMessage
            {
                Player = LocalPlayerId,
                TargetTotalSeconds = targetTotalSeconds,
                Scripted = scripted,
            });
        }

        /// <summary>Takes the next day advance a client asked for. Host only.</summary>
        /// <param name="message">The ask.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeDayAdvance(out DayAdvanceRequestMessage message)
            => _incomingDayAdvance.TryDequeue(out message);

        /// <summary>Takes the next engagement a client reported. Host only.</summary>
        /// <param name="message">The engagement.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeEnemyEngage(out EnemyEngageMessage message)
            => _incomingEnemyEngage.TryDequeue(out message);

        /// <summary>Takes the next engagement the host announced about itself. Clients only.</summary>
        /// <param name="message">The engagement.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeHostEngage(out EnemyEngageMessage message)
            => _incomingHostEngage.TryDequeue(out message);

        /// <summary>Takes the next teammate blow the host relayed into a local brawl. Clients only.</summary>
        /// <param name="message">The blow.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeLeasedDamage(out EnemyDamageMessage message)
            => _incomingLeasedDamage.TryDequeue(out message);

        /// <summary>
        /// Tells every client what the host's own player is doing with one of its NPCs, so a teammate
        /// can join a scripted fight. Host only; the mirror of <see cref="ReportEnemyEngage"/>.
        /// </summary>
        /// <param name="id">Which NPC.</param>
        /// <param name="mode">One of the <see cref="EnemyEngageMessage"/> mode constants.</param>
        public static void AnnounceEnemyEngage(EnemyNetId id, byte mode)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyEngageMessage
            {
                Player = LocalPlayerId,
                NetId = id,
                Mode = mode,
            });
        }

        /// <summary>
        /// Relays the host player's blow into a brawl a client is fighting locally.
        ///
        /// The lease inverts the usual direction: while a client's scripted brawl runs, its copies of
        /// the opponents are the live ones and the host's stand held - so a blow the host lands on its
        /// held copy means nothing until the machine running the fight applies it.
        /// </summary>
        /// <param name="to">The player whose brawl it is.</param>
        /// <param name="id">Which creature.</param>
        /// <param name="amount">What the blow was worth before the defender's side of it.</param>
        /// <param name="poiseDamage">The raw stance figure.</param>
        /// <param name="appliesBleed">Whether the blow may open a wound.</param>
        /// <param name="damageType">Physical, magical or pure.</param>
        public static void SendLeasedEnemyDamage(PlayerId to, EnemyNetId id, int amount, float poiseDamage,
            bool appliesBleed, byte damageType)
        {
            if (State != CoopSessionState.Hosting) return;
            if (amount <= 0) return;

            TrySendToPlayer(BuildEnemyDamage(id, amount, poiseDamage, appliesBleed, damageType, EnemyDamageMessage.FlagBrawl), to);
        }

        private static void OnGVarSnapshotEnd(INetPeer peer, GVarSnapshotEndMessage message)
        {
            if (!IsFromHost()) return;

            World.RemoteGVar.CompleteCatchUp(message.RecordCount, _resyncReplaysUnchanged);
            _resyncReplaysUnchanged = false;
        }

        private static void OnGVarResyncRequest(INetPeer peer, GVarResyncRequestMessage message)
        {
            if (State != CoopSessionState.Hosting) return;

            // The host is loading too - a lost run sends everyone back at once, and a client's smaller load can land
            // first. Answered now, the catch-up is read out of the world being left: the checkpoint test's flag, set
            // after the checkpoint, came back with it.
            if (_hostLoading)
            {
                if (!_resyncAfterLoad.Contains(peer)) _resyncAfterLoad.Add(peer);
                MelonLogger.Msg($"[GVar] player {peer.Id} asked for the world's variables while this world is loading; answering once it has.");
                return;
            }

            MelonLogger.Msg($"[GVar] player {peer.Id} asked for the world's variables again (" +
                (message.Reason == GVarResyncRequestMessage.ReasonSavegameLoaded
                    ? "they loaded a savegame"
                    : "asked for by hand") + "); sending the catch-up.");

            World.GVarSnapshot.SendTo(peer);
            World.JournalShare.SendTo(peer);

            // Map unlocks live in the save like everything else, so a loaded save lost them too.
            World.MapShare.SendTo(peer);
        }

        private static bool _hostLoading;
        private static readonly List<INetPeer> _resyncAfterLoad = new();

        /// <summary>The host's own savegame load began. See <see cref="OnGVarResyncRequest"/>.</summary>
        internal static void HostLoadStarted()
        {
            if (State == CoopSessionState.Hosting) _hostLoading = true;
        }

        /// <summary>The host's own load landed: answer the catch-ups that were asked for meanwhile.</summary>
        internal static void HostLoadFinished()
        {
            _hostLoading = false;
            if (State != CoopSessionState.Hosting)
            {
                _resyncAfterLoad.Clear();
                return;
            }

            foreach (INetPeer peer in _resyncAfterLoad)
            {
                World.GVarSnapshot.SendTo(peer);
                World.JournalShare.SendTo(peer);
                World.MapShare.SendTo(peer);
            }

            if (_resyncAfterLoad.Count > 0) MelonLogger.Msg($"[GVar] this world's load landed; sent the catch-up to {_resyncAfterLoad.Count} waiting player(s).");
            _resyncAfterLoad.Clear();
        }

        /// <summary>
        /// Asks the host to send this world's variables again.
        ///
        /// Loading a savegame inside gameplay rewinds this machine's half of the world without ending
        /// the session, so nothing else would ever put it back.
        /// </summary>
        /// <param name="reason">Why, for the host's log line.</param>
        /// <param name="replayUnchanged">
        /// Whether the consequences of values this world already holds should be run as well. That is
        /// the repair for a save written by a build that stored values without ever running them.
        /// </param>
        public static void RequestResync(byte reason, bool replayUnchanged)
        {
            if (State != CoopSessionState.Joined) return;

            _resyncReplaysUnchanged = replayUnchanged;

            NetworkAccess.SendToAll(new GVarResyncRequestMessage
            {
                Reason = reason,
                ReplayUnchanged = replayUnchanged ? (byte)1 : (byte)0,
            });
        }

        private static void OnResourceSpotSnapshotEnd(INetPeer peer, ResourceSpotSnapshotEndMessage message)
        {
            if (!IsFromHost()) return;

            World.ResourceWork.CompleteSnapshot(message.RecordCount);
        }

        /// <summary>
        /// Tells the session that this player worked a resource spot.
        /// </summary>
        /// <param name="message">The swing.</param>
        public static void ReportResourceWork(in ResourceWorkMessage message)
            => SendIfLive(message);

        /// <summary>Takes the next reported swing, if one is waiting.</summary>
        public static bool TryTakeResourceWork(out ResourceWorkMessage message)
            => _incomingResourceWork.TryDequeue(out message);

        /// <summary>Takes the next chunk of the host's resource-spot table, if one is waiting.</summary>
        public static bool TryTakeResourceSpotSnapshot(out ResourceSpotSnapshotMessage message)
            => _incomingResourceSnapshot.TryDequeue(out message);

        /// <summary>
        /// Tells the session that one rubble pile has left this player's world.
        /// </summary>
        /// <param name="message">The pile, and what happened to it.</param>
        public static void ReportHaulPile(in HaulPileMessage message)
            => SendIfLive(message);

        /// <summary>Takes the next reported pile, if one is waiting.</summary>
        public static bool TryTakeHaulPile(out HaulPileMessage message)
            => _incomingHaulPiles.TryDequeue(out message);

        /// <summary>
        /// Tells the session that a shared pickup or container was looted here. A client tells the
        /// host, which passes it on; the host tells everyone. See <see cref="World.LootShare"/>.
        /// </summary>
        public static void ReportLootTaken(in LootTakenMessage message)
        {
            foreach (LootTakenMessage part in message.Split()) SendIfLive(part);
        }

        /// <summary>
        /// Takes the next chunk of the loot snapshot a joining client was sent, or false when there is
        /// none. Drained by <see cref="World.LootShare.Update"/>.
        /// </summary>
        public static bool TryTakeLootSnapshot(out LootSnapshotMessage message)
            => _incomingLootSnapshot.TryDequeue(out message);

        private static void OnLootTaken(INetPeer peer, LootTakenMessage message)
        {
            if (!message.IsPlausible()) return;
            if (!Gather(peer, ref message)) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (State != CoopSessionState.Hosting) return;

                // Whose take it was is the connection, not the payload. Relayed only under the shared
                // rule: a client that shares on its own has no claim on anybody else's chests.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
                if (LootMode != CoopProtocol.LootModeShared) return;

                message.Player = sender.Value;

                if (message.Kind == LootTakenMessage.KindProposal)
                {
                    // First offer per chest stands. A loser is told what does, alone, so the whole
                    // session is not resent a chest it already agrees on.
                    if (!World.LootShare.TryAcceptProposal(message, out LootTakenMessage? standing))
                    {
                        if (standing.HasValue)
                        {
                            foreach (LootTakenMessage part in standing.Value.Split()) NetworkAccess.Send(peer, part);
                        }

                        return;
                    }

                    foreach (LootTakenMessage part in message.Split()) SendToClientsExcept(part, peer.Id);
                    return;
                }

                World.LootShare.Park(message);
                foreach (LootTakenMessage part in message.Split()) SendToClientsExcept(part, peer.Id);
                return;
            }

            if (!IsFromHost()) return;

            World.LootShare.Park(message);
        }

        /// <summary>
        /// Tells the session that a world object changed here: a door opened, a lever thrown, a crate
        /// smashed. A client tells the host, which passes it on. See <see cref="World.WorldObjects"/>.
        /// </summary>
        public static void ReportWorldObject(in WorldObjectMessage message)
            => SendIfLive(message);

        /// <summary>Host to one joiner: a chunk of the world-object ledger.</summary>
        public static void SendWorldObjectSnapshot(INetPeer peer, WorldObjectSnapshotMessage.Record[] records)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.Send(peer, new WorldObjectSnapshotMessage { Records = records });
        }

        /// <summary>Host to one joiner: the world-object ledger is complete.</summary>
        public static void SendWorldObjectSnapshotEnd(INetPeer peer, ushort count)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.Send(peer, new WorldObjectSnapshotEndMessage { Count = count });
        }

        private static void OnWorldObject(INetPeer peer, WorldObjectMessage message)
        {
            if (!message.IsReplicable()) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (State != CoopSessionState.Hosting) return;

                // Whose change it was is the connection, not the payload.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Player = sender.Value;

                World.WorldObjects.Park(message);
                SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!IsFromHost()) return;

            World.WorldObjects.Park(message);
        }

        /// <summary>
        /// Tells the attacker what this player's own guard did with their blow. Routed by the host when
        /// both ends are clients, the same way the report it answers is.
        /// </summary>
        public static void ReportBlockVerdict(in BlockVerdictMessage message)
        {
            if (State == CoopSessionState.Joined)
            {
                // A client's only peer is the host, which reads it or passes it on.
                NetworkAccess.SendToAll(message);
                return;
            }

            if (State != CoopSessionState.Hosting) return;

            TrySendToPlayer(message, message.Attacker);
        }

        private static void OnBlockVerdict(INetPeer peer, BlockVerdictMessage message)
        {
            if (!message.IsPlausible()) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (State != CoopSessionState.Hosting) return;

                // Who answered is the connection, never the payload: otherwise one client could answer
                // for a blow that was aimed at somebody else.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
                if (message.Victim != sender) return;

                // Addressed to the host itself, or passed on to the player who swung.
                if (message.Attacker == PlayerId.Host)
                {
                    World.RemoteDamage.TakeVerdict(message);
                    return;
                }

                TrySendToPlayer(message, message.Attacker);
                return;
            }

            if (!IsFromHost()) return;
            if (message.Attacker != LocalPlayerId) return;

            World.RemoteDamage.TakeVerdict(message);
        }

        /// <summary>Host to everyone: where the arena is, or that there is none.</summary>
        public static void ReportArenaState(in ArenaStateMessage message)
            => SendIfHost(message);

        /// <summary>Host to one joining player: the arena as it stands.</summary>
        public static void SendArenaState(INetPeer peer, in ArenaStateMessage message)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.Send(peer, message);
        }

        /// <summary>Host to everyone: how a round ended.</summary>
        public static void ReportArenaRound(in ArenaRoundMessage message)
            => SendIfHost(message);

        /// <summary>Host to everyone: the run is lost. See <see cref="World.GameOver"/>.</summary>
        public static void ReportGameOver(ushort checkpoint)
            => SendIfHost(new GameOverMessage { Checkpoint = checkpoint });

        /// <summary>A step of a checkpoint round: host to everyone, or a client to the host. See <see cref="World.Checkpoint"/>.</summary>
        public static void SendCheckpoint(in CheckpointMessage message)
            => SendIfLive(message);

        /// <summary>The local player's own effect ended: to everyone (a client's goes through the host).</summary>
        public static void ReportStatusEnded(in StatusEndedMessage message)
            => SendIfLive(message);

        /// <summary>A line of the local player's conversation: to everyone (a client's goes through the host).</summary>
        public static void ReportDialogueLine(in DialogueLineMessage message)
            => SendIfLive(message);

        /// <summary>A fog patch woke here: to everyone (a client's goes through the host).</summary>
        public static void ReportFogletWake(in FogletWakeMessage message)
            => SendIfLive(message);

        /// <summary>A game-arena gate barrier switched here: to everyone (a client's goes through the host).</summary>
        public static void ReportArenaGate(in ArenaGateMessage message)
            => SendIfLive(message);

        /// <summary>This client's player entered or left a region: to the host.</summary>
        public static void ReportRegion(in RegionMessage message)
        {
            if (State == CoopSessionState.Joined) NetworkAccess.SendToAll(message);
        }

        /// <summary>A creature conjured in this client's leased fight: to the host.</summary>
        public static void ReportLeaseSummon(in LeaseSummonMessage message)
        {
            if (State == CoopSessionState.Joined) NetworkAccess.SendToAll(message);
        }

        /// <summary>A creature's trap laid or gone here: to everyone (a client's goes through the host).</summary>
        public static void ReportTrap(in TrapMessage message)
            => SendIfLive(message);

        /// <summary>An arena team assignment: host to everyone, or a client's request to the host.</summary>
        public static void ReportArenaTeam(in ArenaTeamMessage message)
            => SendIfLive(message);

        /// <summary>Host to one joining player: a team as it stands.</summary>
        public static void SendArenaTeam(INetPeer peer, in ArenaTeamMessage message)
        {
            if (State == CoopSessionState.Hosting) NetworkAccess.Send(peer, message);
        }

        /// <summary>An invite step for one player, routed like a trade.</summary>
        public static void SendInvite(InviteMessage message)
        {
            message.From = LocalPlayerId;
            if (State == CoopSessionState.Hosting)
            {
                if (message.To == LocalPlayerId) World.Invites.Take(message);
                else TrySendToPlayer(message, message.To);
            }
            else if (IsLive)
            {
                NetworkAccess.SendToAll(message);
            }
        }

        private static void OnInvite(INetPeer peer, InviteMessage message)
        {
            if (State == CoopSessionState.Hosting)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender) || message.To == sender) return;
                message.From = sender;

                if (message.To == LocalPlayerId) World.Invites.Take(message);
                else TrySendToPlayer(message, message.To);

                // A yes starts something only the host can do, wherever the two players are.
                if (message.Stage == InviteMessage.StageYes) World.Invites.HostCarriesOut(message);
                return;
            }

            if (IsFromHost() && message.To == LocalPlayerId) World.Invites.Take(message);
        }

        /// <summary>A trade step for one player: to the host, which passes it on, or straight to them from the host.</summary>
        public static void SendTrade(TradeMessage message)
        {
            message.From = LocalPlayerId;
            if (State == CoopSessionState.Hosting) TrySendToPlayer(message, message.To);
            else if (IsLive) NetworkAccess.SendToAll(message);
        }

        private static void OnTrade(INetPeer peer, TradeMessage message)
        {
            if (State == CoopSessionState.Hosting)
            {
                // Who is trading is the connection, not the payload.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender) || message.To == sender) return;
                message.From = sender;

                if (message.To == LocalPlayerId) World.Trade.Take(message);
                else TrySendToPlayer(message, message.To);
                return;
            }

            if (IsFromHost() && message.To == LocalPlayerId) World.Trade.Take(message);
        }

        /// <summary>How many teammates are connected to this host.</summary>
        internal static int PeerCount => _peerToPlayer.Count;

        /// <summary>
        /// Tells the session what a breakable cache spilled, or what is left of it. See
        /// <see cref="World.CacheShare"/>.
        /// </summary>
        public static void ReportCacheSpill(in CacheSpillMessage message)
            => SendIfLive(message);

        /// <summary>Host to one joining player: a cache as the session agrees it stands.</summary>
        public static void SendCacheSpill(INetPeer peer, in CacheSpillMessage message)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.Send(peer, message);
        }

        private static void OnCacheSpill(INetPeer peer, CacheSpillMessage message)
        {
            if (!message.IsPlausible()) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (State != CoopSessionState.Hosting) return;
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
                if (LootMode != CoopProtocol.LootModeShared) return;

                message.Player = sender.Value;

                if (message.Kind == CacheSpillMessage.KindOffer)
                {
                    // First offer per cache stands, and a loser is told what does rather than the whole
                    // session being resent a cache it already agrees on.
                    if (!World.CacheShare.TryAcceptOffer(message, out CacheSpillMessage? standing))
                    {
                        if (standing.HasValue) NetworkAccess.Send(peer, standing.Value);
                        return;
                    }

                    SendToClientsExcept(message, peer.Id);
                    return;
                }

                World.CacheShare.Park(message);
                SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!IsFromHost()) return;

            World.CacheShare.Park(message);
        }

        private static void OnArenaState(INetPeer peer, ArenaStateMessage message)
        {
            // The arena is the host's to mark, so a client saying otherwise is ignored rather than
            // argued with.
            if (!IsFromHost()) return;

            World.Arena.Take(message);
        }

        private static void OnArenaRound(INetPeer peer, ArenaRoundMessage message)
        {
            if (!IsFromHost()) return;

            World.Arena.TakeRound(message);
        }

        private static void OnWorldObjectSnapshot(INetPeer peer, WorldObjectSnapshotMessage message)
        {
            if (!IsFromHost()) return;

            World.WorldObjects.TakeSnapshot(message);
        }

        private static void OnWorldObjectSnapshotEnd(INetPeer peer, WorldObjectSnapshotEndMessage message)
        {
            if (!IsFromHost()) return;

            World.WorldObjects.CompleteSnapshot(message.Count);
        }

        private static void OnLootSnapshotEnd(INetPeer peer, LootSnapshotEndMessage message)
        {
            if (!IsFromHost()) return;

            World.LootShare.CompleteSnapshot(message.Count);
        }

        /// <summary>
        /// Tells the session what this player's bars are out of. Sent by the broadcaster on change and
        /// when the roster grows; see <see cref="PlayerVitalsMessage"/>.
        /// </summary>
        public static void BroadcastVitals(ushort maxHealth, ushort maxStamina, byte flags)
        {
            if (!IsLive) return;

            PlayerVitalsMessage message = new()
            {
                PlayerId = LocalPlayerId,
                MaxHealth = maxHealth,
                MaxStamina = maxStamina,
                Flags = flags,
            };

            _vitals[LocalPlayerId] = message;
            NetworkAccess.SendToAll(message);
        }

        private static void OnPlayerVitals(INetPeer peer, PlayerVitalsMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.PlayerId = sender;
                _vitals[sender] = message;

                SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            _vitals[message.PlayerId] = message;
        }

        private static void OnNpcPlacementRequest(INetPeer peer, NpcPlacementRequestMessage message)
        {
            // Only the host holds the placements everybody follows.
            if (State != CoopSessionState.Hosting) return;

            World.NpcPlacement.Answer(peer, message);
        }

        private static void OnWeather(INetPeer peer, WeatherMessage message)
        {
            if (!IsFromHost()) return;

            // Superseded by the next report, like enemy health: the front of the queue is the least
            // useful thing in it.
            EnqueueLatest(_incomingWeather, message, 4);
        }

        private static void OnDaytime(INetPeer peer, DaytimeMessage message)
        {
            if (!IsFromHost()) return;

            // Superseded by the next report, like the weather.
            EnqueueLatest(_incomingDaytime, message, 4);
        }

        private static void OnGVarSnapshot(INetPeer peer, GVarSnapshotMessage message)
        {
            if (!IsFromHost()) return;
            if (message.Records == null) return;

            // Exploded into ordinary changes, but into the catch-up queue rather than the live one. The
            // apply side must know the difference: a live change may be allowed to play its transition,
            // a catch-up never is, because it is history.
            foreach (GVarSnapshotMessage.Record record in message.Records)
            {
                Enqueue(_incomingGVarCatchup, new GVarChangeMessage
                {
                    ListGuid = message.ListGuid,
                    VarId = record.VarId,
                    Kind = record.Kind,
                    IntValue = record.IntValue,
                    FloatValue = record.FloatValue,
                }, MaxPendingGVarCatchup, "gvar catch-up records");
            }
        }

        private static void OnEnemyHealth(INetPeer peer, EnemyHealthMessage message)
        {
            if (!IsFromHost()) return;

            // Dropping the oldest, unlike everything else about creatures. A health figure is superseded
            // by the next one, so the front of this queue is the least useful thing in it - which is the
            // damage rule rather than the knowledge rule, and the reasoning is the same as there.
            EnqueueLatest(_incomingEnemyHealth, message, MaxPendingDamage);
        }

        private static void OnEnemyDamage(INetPeer peer, EnemyDamageMessage message)
        {
            // The host relaying a teammate's blow into a brawl this player is fighting locally. Deduped
            // by sequence exactly as the host dedupes this message in the other direction.
            if (State == CoopSessionState.Joined)
            {
                if (!IsFromHost()) return;

                if (_lastLeasedDamageSequence >= message.Sequence) return;
                _lastLeasedDamageSequence = message.Sequence;

                EnqueueLatest(_incomingLeasedDamage, message, MaxPendingDamage);
                return;
            }

            if (State != CoopSessionState.Hosting) return;

            // The sender does not get to name the attacker, the same substitution every client-to-host
            // message here makes.
            if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

            message.Attacker = sender;

            // Already counted. The transport is reliable, so this is a resend rather than a duplicate -
            // but counting it twice would double a player's damage, which is worse than dropping it.
            if (_lastEnemyDamageSequence.TryGetValue(sender, out uint last) && message.Sequence <= last) return;

            _lastEnemyDamageSequence[sender] = message.Sequence;

            EnqueueLatest(_incomingEnemyDamage, message, MaxPendingDamage);
        }

        private static void OnDayAdvanceRequest(INetPeer peer, DayAdvanceRequestMessage message)
        {
            // Only the host turns a world over, so only the host listens. A client hearing this
            // through the relay is hearing somebody else's ask, which is none of its business.
            if (State != CoopSessionState.Hosting) return;

            if (!message.IsPlausible()) return;

            // The sender does not get to name the player, the same substitution every client-to-host
            // message here makes.
            if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

            message.Player = sender;

            // Keep the OLDEST rather than the newest, which is the opposite of every other queue here
            // and is deliberate: these are not reports of a state that supersede one another, they are
            // requests to move the world forward, and dropping the old one to keep a newer one would
            // skip a night rather than delay one. A backlog beyond a couple is a client repeating
            // itself, and repeating is what the host's own cooldown is there to absorb.
            Enqueue(_incomingDayAdvance, message, 4);
        }

        private static void OnEnemyEngage(INetPeer peer, EnemyEngageMessage message)
        {
            // The host's own engagements, announced so a teammate can join the fight. Nothing but the
            // host may say this; another client's report arriving through the relay is not the host's
            // word and is dropped.
            if (State == CoopSessionState.Joined)
            {
                if (!IsFromHost()) return;

                EnqueueLatest(_incomingHostEngage, message, MaxPendingDamage);
                return;
            }

            if (State != CoopSessionState.Hosting) return;

            // The sender does not get to name the player, the same substitution every client-to-host
            // message here makes.
            if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

            message.Player = sender;

            // Keep-latest like health, because a newer engagement statement supersedes an older one.
            EnqueueLatest(_incomingEnemyEngage, message, MaxPendingDamage);
        }

        /// <summary>
        /// Queues something nothing will repeat. Full, it refuses the newest, and says so when
        /// <paramref name="what"/> names the queue.
        /// </summary>
        /// <returns>Whether the message was queued.</returns>
        private static bool Enqueue<T>(Queue<T> queue, in T message, int cap, string? what = null) where T : struct
        {
            if (queue.Count >= cap)
            {
                if (what != null) MelonLogger.Warning($"[Enemy] more than {cap} {what} are waiting; the newest was refused.");

                return false;
            }

            queue.Enqueue(message);
            return true;
        }

        /// <summary>
        /// Queues something the next message supersedes. Full, it drops the oldest, which is the least
        /// useful thing waiting.
        /// </summary>
        private static void EnqueueLatest<T>(Queue<T> queue, in T message, int cap) where T : struct
        {
            if (queue.Count >= cap)
            {
                queue.Dequeue();
            }

            queue.Enqueue(message);
        }

        private static void SendIfLive<T>(in T message) where T : struct, INetMessage
        {
            if (IsLive) NetworkAccess.SendToAll(message);
        }

        private static void SendIfHost<T>(in T message) where T : struct, INetMessage
        {
            if (State == CoopSessionState.Hosting) NetworkAccess.SendToAll(message);
        }

        /// <summary>
        /// Tells a player that this machine put a status effect on their body.
        ///
        /// Reliable, and sent once per application rather than continuously: an effect is something that
        /// happened, not a value being sampled, so a lost one is a poison nobody ever feels.
        /// </summary>
        /// <param name="target">Whose body took it.</param>
        /// <param name="effectId">The guid of the status effect asset.</param>
        /// <param name="origin">Whether a player or one of the world's creatures applied it, on the same
        /// terms as <see cref="ReportDamage"/>: the friendly-fire rule decides whether a teammate may
        /// poison a teammate and has nothing to say about a spider that did.</param>
        public static void ReportStatusEffect(PlayerId target, string effectId,
            byte origin = DamageReportMessage.OriginPlayer)
        {
            if (!IsLive) return;

            // The same rule the damage takes, and with the same exception. An effect applied by a player
            // is a hostile act and a session with friendly fire off should not be able to poison anyone
            // through the side door; an effect applied by a creature is the world doing its job.
            if (origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(target)) return;

            if (!target.IsAssigned || target == LocalPlayerId) return;
            if (string.IsNullOrEmpty(effectId)) return;

            StatusEffectMessage message = new()
            {
                Target = target,
                Source = LocalPlayerId,
                EffectId = effectId,
                Sequence = NextStatusSequenceFor(target),
                Origin = origin,
            };

            if (NetworkAccess.Role == NetRole.Host)
            {
                TrySendToPlayer(message, target);
                return;
            }

            NetworkAccess.SendToAll(message);
        }

        private static byte NextStatusSequenceFor(PlayerId target)
        {
            byte next = _nextStatusSequence.TryGetValue(target, out byte last) ? unchecked((byte)(last + 1)) : (byte)1;
            _nextStatusSequence[target] = next;

            return next;
        }

        /// <summary>
        /// Takes the next status effect that has arrived for the local player, oldest first.
        /// </summary>
        /// <param name="message">The effect, when there is one.</param>
        /// <returns>False when nothing is waiting.</returns>
        public static bool TryTakeStatusEffect(out StatusEffectMessage message)
            => _incomingEffects.TryDequeue(out message);

        private static void OnStatusEffect(INetPeer peer, StatusEffectMessage message)
        {
            if (!IsLive) return;
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Source = sender;

                // The pair actually involved, as for a blow - see OnDamageReport.
                if (message.Origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(sender, message.Target)) return;

                if (message.Target == sender) return;

                if (message.Target == LocalPlayerId)
                {
                    EnqueueEffect(message);
                    return;
                }

                if (!_players.ContainsKey(message.Target)) return;

                TrySendToPlayer(message, message.Target);
                return;
            }

            if (!IsFromHost()) return;
            if (message.Target != LocalPlayerId) return;
            if (message.Origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(message.Source)) return;

            EnqueueEffect(message);
        }

        /// <summary>
        /// Records an effect for the local player, once.
        /// </summary>
        private static void EnqueueEffect(in StatusEffectMessage message)
        {
            // Per source, and by the same high-water mark the damage uses: applying an effect twice is a
            // second stack of poison rather than a harmless repeat, so a replayed packet has to be
            // refused rather than merely being unlikely.
            if (_lastStatusSequence.TryGetValue(message.Source, out byte last)
                && !PlayerStateMessage.IsNewerAction(message.Sequence, last))
            {
                return;
            }

            _lastStatusSequence[message.Source] = message.Sequence;

            EnqueueLatest(_incomingEffects, message, MaxPendingDamage);
        }

        /// <summary>
        /// Stops the transport and clears everything that describes a session, without deciding what state
        /// the mod ends up in. Both leaving and failing need this much; only they know what comes after.
        /// </summary>
        private static void Teardown(bool announce)
        {
            // A load that never landed must not hold the next session's catch-ups.
            _hostLoading = false;
            _resyncAfterLoad.Clear();

            if (NetworkAccess.Role != NetRole.None)
            {
                NetworkAccess.Stop();

                if (announce)
                {
                    MelonLogger.Msg("[Coop] Left the session.");
                }
            }

            _players.Clear();
            _latestStates.Clear();
            _vitals.Clear();
            _equipment.Clear();
            _talents.Clear();
            _equipmentParts.Clear();
            _talentParts.Clear();
            _lootParts.Clear();
            _itemUses.Clear();
            _peerToPlayer.Clear();
            _roundTripMs.Clear();
            _pingSentAt.Clear();
            _incomingDamage.Clear();
            _incomingEffects.Clear();
            _incomingKnowledge.Clear();
            _incomingExperience.Clear();
            _incomingSummonBinds.Clear();
            _incomingEnemyDeaths.Clear();

            // Cleared with the rest, and worth saying why: the set is what stops a death being reported
            // twice, and the thing it guards against is a creature dying repeatedly within one session.
            // Across sessions the save is what remembers, so carrying it would only make a second session
            // in the same world refuse to report deaths it has to report again.
            _deadEnemies.Clear();
            _incomingBinds.Clear();
            _incomingReleases.Clear();
            _incomingGraveyard.Clear();
            _incomingAlive.Clear();
            _incomingRevive.Clear();
            _incomingGVars.Clear();
            _incomingGVarCatchup.Clear();
            _incomingResourceWork.Clear();
            _incomingLootSnapshot.Clear();
            _incomingResourceSnapshot.Clear();
            _incomingHaulPiles.Clear();
            _incomingCutscenes.Clear();
            _incomingActorPorts.Clear();
            _incomingWeather.Clear();
            _incomingJournal.Clear();
            _incomingJournalTopics.Clear();
            _incomingNpcPlacements.Clear();
            _incomingDaytime.Clear();
            _incomingMapUnlocks.Clear();
            _incomingDialogueExp.Clear();
            _incomingDialogueItems.Clear();
            _incomingEnemyHealth.Clear();
            _incomingEnemyDamage.Clear();
            _incomingEnemyEngage.Clear();
            _incomingHostEngage.Clear();
            _incomingLeasedDamage.Clear();
            _incomingDayAdvance.Clear();
            _lastLeasedDamageSequence = 0;
            _incomingSnapshots.Clear();

            // Including the spawns, which were being carried across sessions: a spawn built after a
            // session ends is a creature standing in a world its announcement no longer describes.
            _incomingSpawns.Clear();
            _incomingDrives.Clear();
            _lastEnemyDamageSequence.Clear();
            _nextEnemyDamageSequence = 0;
            _lastStatusSequence.Clear();
            _lastDamageSequence.Clear();
            _nextDamageSequence = 0;
            _nextStatusSequence.Clear();
            _clock.Reset();
            LocalPlayerId = PlayerId.None;
            _connectingSeconds = 0f;
            ImplausibleStates = 0;
            _sincePing = 0f;

            // Back to the safe answers rather than left on whatever the last session decided. Anything
            // that builds a body consults these, and outside a session there is no host to have an
            // opinion.
            AdoptRules(false, CoopProtocol.DefaultRecoveryHealthPercent, CoopProtocol.DefaultEnemyHealthPercent, CoopProtocol.LootModeInstanced);
        }

        /// <summary>
        /// Ends the session and leaves a reason a player can read. Ordered so the reason outlives the
        /// teardown: <see cref="Leave"/> clears <see cref="LastError"/>, so setting it first would lose it.
        /// </summary>
        private static void Fail(string reason)
        {
            Teardown(announce: false);

            LastError = reason;
            State = CoopSessionState.Failed;
            MelonLogger.Error("[Coop] " + reason);
        }

        private static bool Begin()
        {
            Leave();

            // The one place every way of starting a session passes through, which is what makes a
            // withdrawn build stay withdrawn whichever button or command asked.
            string? refusal = VersionCheck.Refusal;
            if (refusal != null)
            {
                Fail(refusal);
                return false;
            }

            State = CoopSessionState.Connecting;

            // Told here rather than on the accepted handshake, so the first save of a session that is
            // still connecting is already kept as the state from before it.
            SaveBackups.NoteSessionBegan();

            return true;
        }

        private static void OnHostStarted()
        {
            State = CoopSessionState.Hosting;

            // The host's settings are the session's rules from this moment on. TickRules keeps the two in
            // step for as long as the session lasts; this is only where they first meet.
            AdoptRules(CoopPreferences.FriendlyFire, CoopPreferences.RecoveryHealthPercent, CoopPreferences.EnemyHealthPercent, CoopPreferences.LootMode);

            // The host is player 0 by definition and needs no handshake to learn it.
            LocalPlayerId = PlayerId.Host;
            _players[PlayerId.Host] = Sanitize(PlayerIdentity.LocalName(), PlayerId.Host);

            string where = SessionCode != null ? $"session code {SessionCode}" : "a direct session";
            string sealing = IsEncrypted ? "encrypted" : "not encrypted, anyone relaying can read it";
            MelonLogger.Msg($"[Coop] Hosting {where}, {sealing}.");
        }

        private static void OnConnectedToHost()
        {
            // Not "joined" yet: the host still has to accept the version. Claiming success here would show
            // the player a session they are about to be dropped from.
            HandshakeMessage handshake = new()
            {
                ProtocolVersion = ProtocolVersion,
                PlayerName = PlayerIdentity.LocalName() ?? string.Empty,
                ModSetHash = ModSet.Hash(),
            };

            NetworkAccess.SendToAll(handshake);
            MelonLogger.Msg("[Coop] Connected, waiting for the host to accept the handshake.");
        }

        private static void OnPeerConnected(INetPeer peer)
        {
            MelonLogger.Msg($"[Coop] Peer {peer.Id} connected.");
        }

        private static void OnPeerDisconnected(INetPeer peer, DisconnectReason reason)
        {
            MelonLogger.Msg($"[Coop] Peer {peer.Id} left: {reason}.");

            // A host losing a client keeps going; that is most of the point of being the host. It does have
            // to tell everyone else, because nothing else can: only the host sees transport events for a
            // peer that is not itself.
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (_peerToPlayer.Remove(peer.Id, out PlayerId departed))
                {
                    ForgetPlayer(departed);
                    World.Checkpoint.PeerLeft(peer.Id);

                    PlayerLeftMessage left = new() { Id = departed };
                    NetworkAccess.SendToAll(left);
                }

                return;
            }

            // Anything else is a client that has lost the host, so there is no session left to be in.
            if (State != CoopSessionState.Connecting && State != CoopSessionState.Joined) return;

            if (reason == DisconnectReason.Kicked)
            {
                Fail("The host removed you from the session.");
                return;
            }

            // Going through Leave rather than just setting Idle, because the transport is still up and in
            // Client role at this point. Reporting Idle while it runs on is a state nothing else expects.
            Leave();
        }

        /// <summary>
        /// Keeps the session's rules in step with the host's settings, and tells everyone when they move.
        ///
        /// The host polls rather than being notified, because the setting it watches lives in the game's
        /// own gameplay config and that config has no change event to subscribe to. A comparison of one
        /// bool per frame is cheaper than the machinery that would avoid it.
        ///
        /// Clients do nothing here. They are told, and the rule they were told is the only one that counts
        /// - a client reading its own setting is exactly the disagreement the message exists to prevent.
        /// </summary>
        private static void TickRules()
        {
            if (NetworkAccess.Role != NetRole.Host) return;

            bool friendlyFire = CoopPreferences.FriendlyFire;
            byte recovery = CoopPreferences.RecoveryHealthPercent;
            ushort enemyHealth = CoopPreferences.EnemyHealthPercent;
            byte lootMode = CoopPreferences.LootMode;

            if (friendlyFire == FriendlyFire && recovery == RecoveryHealthPercent && enemyHealth == EnemyHealthPercent && lootMode == LootMode) return;

            AdoptRules(friendlyFire, recovery, enemyHealth, lootMode);

            // The whole set, not the field that moved. A rule message that carried only a change would
            // have to arrive, and arrive in order, to leave the far end correct; one that carries
            // everything is right on its own whenever it lands.
            NetworkAccess.SendToAll(new SessionRulesMessage
            {
                FriendlyFire = FriendlyFire,
                RecoveryHealthPercent = RecoveryHealthPercent,
                EnemyHealthPercent = EnemyHealthPercent,
                LootMode = LootMode,
            });
        }

        private static void OnSessionRules(INetPeer peer, SessionRulesMessage message)
        {
            // Only the host sets rules. Without this a host would apply a client's message and let any
            // peer decide who in the session can be killed.
            if (NetworkAccess.Role != NetRole.Client) return;
            if (State != CoopSessionState.Joined) return;

            AdoptRules(message.FriendlyFire, message.RecoveryHealthPercent, message.EnemyHealthPercent, message.LootMode);
        }

        /// <summary>
        /// Records the session's rules and says so, once per actual change.
        ///
        /// Everything that acts on these reconciles against the properties on its own schedule, so there
        /// is nothing to notify - but rules that decide whether your friend can kill you, and what you
        /// stand up with afterwards, should never change silently.
        /// </summary>
        /// <param name="friendlyFire">Whether players may damage each other.</param>
        /// <param name="recoveryHealthPercent">
        /// What a downed player stands up with. Zero is treated as "not stated" and replaced with the
        /// protocol default: a host that never set it is not asking for players to stand up on nothing.
        /// </param>
        /// <param name="enemyHealthPercent">
        /// How tough creatures are, as a percentage of authored health. Zero is "not stated" and reads
        /// as the game as shipped; anything else is held to the protocol's range, so a peer cannot send
        /// a number that makes every blow land nothing.
        /// </param>
        /// <param name="lootMode">
        /// Whether loot is shared. Anything but the shared value reads as the game as shipped, so a
        /// peer cannot talk this machine into a rule it does not have.
        /// </param>
        private static void AdoptRules(bool friendlyFire, byte recoveryHealthPercent, ushort enemyHealthPercent, byte lootMode)
        {
            if (recoveryHealthPercent == 0)
            {
                recoveryHealthPercent = CoopProtocol.DefaultRecoveryHealthPercent;
            }

            if (recoveryHealthPercent > 100)
            {
                recoveryHealthPercent = 100;
            }

            if (enemyHealthPercent == 0)
            {
                enemyHealthPercent = CoopProtocol.DefaultEnemyHealthPercent;
            }

            enemyHealthPercent = Math.Clamp(enemyHealthPercent, CoopProtocol.MinEnemyHealthPercent, CoopProtocol.MaxEnemyHealthPercent);

            if (friendlyFire != FriendlyFire)
            {
                FriendlyFire = friendlyFire;
                MelonLogger.Msg($"[Coop] Friendly fire is now {(friendlyFire ? "ON - players can hurt each other" : "off")}.");
            }

            if (recoveryHealthPercent != RecoveryHealthPercent)
            {
                RecoveryHealthPercent = recoveryHealthPercent;
                MelonLogger.Msg($"[Coop] A downed player now stands up with {recoveryHealthPercent}% health.");
            }

            if (enemyHealthPercent != EnemyHealthPercent)
            {
                EnemyHealthPercent = enemyHealthPercent;
                MelonLogger.Msg($"[Coop] Creatures now have {enemyHealthPercent}% of their normal health.");
            }

            if (lootMode != CoopProtocol.LootModeShared) lootMode = CoopProtocol.LootModeInstanced;

            if (lootMode != LootMode)
            {
                LootMode = lootMode;
                MelonLogger.Msg(lootMode == CoopProtocol.LootModeShared
                    ? "[Coop] Loot is now shared: a pickup taken is gone for everyone, and a chest holds what the last player left."
                    : "[Coop] Loot is now per player, as the game ships it.");
            }
        }

        private static void OnConnectionRejected(ConnectionRejection rejection)
        {
            Fail(Describe(rejection));
        }

        private static void OnNetworkError(string description)
        {
            MelonLogger.Error("[Coop] Transport error: " + description);
        }

        private static void OnHandshake(INetPeer peer, HandshakeMessage message)
        {
            // Only a host has any business answering a handshake. Every message type is registered on both
            // sides, so who is allowed to say what has to be checked here or not at all.
            if (NetworkAccess.Role != NetRole.Host) return;

            // One handshake per peer. Repeating it costs the sender nothing and would otherwise hand out a
            // second id to somebody who already has one.
            if (_peerToPlayer.ContainsKey(peer.Id)) return;

            if (message.ProtocolVersion != ProtocolVersion)
            {
                Refuse(peer, HandshakeRefusal.ProtocolMismatch,
                    $"coop protocol {message.ProtocolVersion}, this host speaks {ProtocolVersion}");
                return;
            }

            if (!TryAssignPlayerId(out PlayerId assigned))
            {
                Refuse(peer, HandshakeRefusal.SessionFull, $"session already holds {CoopProtocol.MaxClients} clients");
                return;
            }

            string name = Sanitize(message.PlayerName, assigned);
            _peerToPlayer[peer.Id] = assigned;
            _players[assigned] = name;

            // The host does not change state when somebody joins it, but it does stop being alone - which is
            // the whole of what SinglePlayerOnly work is gated on.
            CoopProvider.NotifyChanged();

            // A zero hash means the other side did not compute one, which is no opinion rather than a
            // disagreement.
            bool modSetMatches = message.ModSetHash == 0u || message.ModSetHash == ModSet.Hash();
            if (!modSetMatches)
            {
                MelonLogger.Warning($"[Coop] {name} is running a different set of mods. This is allowed, but anything that changes " +
                    "items, stats or the world will only apply on one side.");
                ModSet.Report();
            }

            HandshakeResultMessage result = new()
            {
                Accepted = true,
                HostProtocolVersion = ProtocolVersion,
                AssignedPlayerId = assigned,
                Refusal = HandshakeRefusal.None,
                ModSetMatches = modSetMatches,

                // The values latched when hosting started, not the settings as they read now. Re-reading here
                // would hand a late joiner a different rule than the players already in the session, which
                // is exactly the asymmetry the rules travelling with the handshake exist to prevent.
                Rules = new SessionRulesMessage
                {
                    FriendlyFire = FriendlyFire,
                    RecoveryHealthPercent = RecoveryHealthPercent,
                    EnemyHealthPercent = EnemyHealthPercent,
                    LootMode = LootMode,
                },
            };

            NetworkAccess.Send(peer, result);

            // The newcomer gets everyone already in; everyone already in gets told about the newcomer.
            // Both are needed and neither covers the other.
            NetworkAccess.Send(peer, BuildRoster(exclude: assigned));

            PlayerJoinedMessage joined = new() { Id = assigned, Name = name };
            SendToClientsExcept(joined, peer.Id);

            // Everything already dead, because nothing will ever mention it again. A creature killed
            // before this player connected is absent from the conversation from here on, and the game
            // reads an absent save record as "alive" - so without this the newcomer walks past every camp
            // the host has cleared and finds it standing. The save's own corpses ride along: a creature
            // that died before any session existed is in no session graveyard, and used to fall on other
            // machines only once the host streamed its corpse in.
            HashSet<string> graveyard = new(_deadEnemies, StringComparer.Ordinal);
            World.EnemyRegistry.CollectSaveDead(graveyard);
            SendGraveyard(peer, graveyard);

            // The world's variables, for the same reason the graveyard goes: quest state set before this
            // player connected is never mentioned again on its own.
            World.GVarSnapshot.SendTo(peer);

            // The sky as it stands, so the newcomer is not waiting under the wrong weather for the next
            // periodic report.
            if (World.WeatherSync.TryBuildCurrent(out WeatherMessage weather))
            {
                NetworkAccess.Send(peer, weather);
            }

            // The whole journal, because the quest graphs that write it never cross: without this a
            // joining player's journal stays empty however completely the variables arrive.
            World.JournalShare.SendTo(peer);

            // The clock, so the newcomer lives through this world's day from the first frame.
            if (World.DaytimeSync.TryBuildCurrent(out DaytimeMessage daytime))
            {
                NetworkAccess.Send(peer, daytime);
            }

            // The maps this world can read, because a dialogue handed them to one player only.
            World.MapShare.SendTo(peer);
            World.ResourceWork.SendTo(peer);
            World.LootShare.SendTo(peer);
            World.WorldObjects.SendTo(peer);
            World.Arena.SendTo(peer);
            World.CacheShare.SendTo(peer);

            MelonLogger.Msg($"[Coop] {name} joined as {assigned} ({_players.Count} in session).");
        }

        private static void OnHandshakeResult(INetPeer peer, HandshakeResultMessage message)
        {
            // Only a client is owed a handshake result. Without this check a host handles its own clients'
            // results: any peer could send a refusal and the host would tear down its own session for
            // everyone, which is a denial of service costing the sender one packet.
            if (NetworkAccess.Role != NetRole.Client) return;

            // And only while a join is actually in flight, so a second result cannot unseat a live session.
            if (State != CoopSessionState.Connecting) return;

            if (!message.Accepted)
            {
                Fail(Describe(message));
                return;
            }

            State = CoopSessionState.Joined;
            _connectingSeconds = 0f;

            LocalPlayerId = message.AssignedPlayerId;
            _players[LocalPlayerId] = Sanitize(PlayerIdentity.LocalName(), LocalPlayerId);

            // Adopted rather than read locally: these are the host's session rules, and a client that
            // kept its own answers could be a legal target for someone it could not hit back, or stand
            // up with more health than the player who put it down.
            AdoptRules(message.Rules.FriendlyFire, message.Rules.RecoveryHealthPercent, message.Rules.EnemyHealthPercent, message.Rules.LootMode);

            MelonLogger.Msg($"[Coop] The host accepted the handshake; this client is {LocalPlayerId}.");

            if (!message.ModSetMatches)
            {
                MelonLogger.Warning("[Coop] The host is running a different set of mods. This is allowed, but anything that changes " +
                    "items, stats or the world will only apply on one side. Compare the list below with theirs.");
                ModSet.Report();
            }
        }

        private static void OnRosterSnapshot(INetPeer peer, RosterSnapshotMessage message)
        {
            if (!IsFromHost()) return;

            foreach (RosterEntry entry in message.Players ?? [])
            {
                if (entry.Id == LocalPlayerId) continue;

                _players[entry.Id] = Sanitize(entry.Name, entry.Id);
            }

            MelonLogger.Msg($"[Coop] Roster received: {_players.Count} in session.");
        }

        private static void OnPlayerJoined(INetPeer peer, PlayerJoinedMessage message)
        {
            if (!IsFromHost()) return;
            if (message.Id == LocalPlayerId) return;

            _players[message.Id] = Sanitize(message.Name, message.Id);

            CoopProvider.NotifyChanged();

            MelonLogger.Msg($"[Coop] {_players[message.Id]} joined as {message.Id} ({_players.Count} in session).");
        }

        private static void OnPlayerLeft(INetPeer peer, PlayerLeftMessage message)
        {
            if (!IsFromHost()) return;

            ForgetPlayer(message.Id);
        }

        /// <summary>
        /// Drops everything this machine holds about a player who has left.
        /// </summary>
        private static void ForgetPlayer(PlayerId player)
        {
            _players.Remove(player);

            // Ids are reused: the next joiner must not inherit a departed player's arena team.
            if (State == CoopSessionState.Hosting) World.Arena.SetTeam(player, 0);

            // The last one leaving makes the host alone again, and a mod that stopped for the session has
            // no other way to hear that it may start.
            CoopProvider.NotifyChanged();

            _latestStates.Remove(player);
            _vitals.Remove(player);
            _equipment.Remove(player);
            _talents.Remove(player);
            _equipmentParts.Remove(player);
            _talentParts.Remove(player);
            _itemUses.Remove(player);

            // Forgotten with them, because an id is reused as soon as it is free and the next player to
            // hold it starts counting blows from one again. A high-water mark left behind would swallow
            // every hit they land until they caught up with a stranger.
            _lastDamageSequence.Remove(player);
            _lastStatusSequence.Remove(player);
            World.RemoteGVar.ForgetOrigin(player.Value);

            // A rubble pile they carried off and never dumped is a debt this world can no longer collect.
            // Put those piles back so this world can haul them itself.
            World.HaulShare.RepayOrphans(player.Value);

            MelonLogger.Msg($"[Coop] {player} left ({_players.Count} in session).");
        }

        private static void OnPlayerState(INetPeer peer, PlayerStateMessage message)
        {
            // Before anything reads it, and on both sides. A NaN coordinate does not merely misplace a
            // body, it makes the body unrecoverable: the value lands in the transform and is then the
            // input to the next frame's interpolation, so every frame after it is NaN as well. Checking
            // here rather than where the numbers are used is also what keeps a host from passing one on -
            // storing it and relaying it would break every other client in the session on behalf of
            // whoever sent it.
            //
            // Counted rather than logged. This is reachable by any peer in the session, and a line per
            // packet is how a remote player fills somebody else's disk.
            if (!message.IsPlausible())
            {
                ImplausibleStates++;
                return;
            }

            if (NetworkAccess.Role == NetRole.Host)
            {
                // The sender does not get to say who it is. Overwriting the claimed id with the one this
                // host handed that peer is the difference between a client describing itself and a client
                // describing - and therefore moving - somebody else.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.PlayerId = sender;
                _latestStates[sender] = message;

                // Relayed peer by peer rather than broadcast, because the sender must not receive its own
                // state back and the API has no exclusion primitive. At these player counts the extra
                // packets are irrelevant, and it keeps the mod off a blocked API and relay change.
                SendToClientsExcept(message, peer.Id, Delivery.Sequenced);
                return;
            }

            // A client trusts what the host stamped, because the host is the authority on identity. Its own
            // state coming back would be an echo it has no use for.
            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            _latestStates[message.PlayerId] = message;
        }

        /// <summary>
        /// Whether the message being handled came from the host. Clients accept roster and state changes
        /// only from there; in a relay session a client can receive packets from other clients too.
        /// </summary>
        private static bool IsFromHost()
        {
            return NetworkAccess.Role == NetRole.Client && State == CoopSessionState.Joined;
        }

        /// <summary>
        /// Finds the lowest free client id. Lowest rather than a running counter, so a session where
        /// players come and go does not run out of ids while holding two players.
        /// </summary>
        private static bool TryAssignPlayerId(out PlayerId assigned)
        {
            for (byte candidate = 1; candidate <= CoopProtocol.MaxClients; candidate++)
            {
                PlayerId id = new(candidate);
                if (_players.ContainsKey(id)) continue;

                assigned = id;
                return true;
            }

            assigned = PlayerId.None;
            return false;
        }

        private static RosterSnapshotMessage BuildRoster(PlayerId exclude)
        {
            List<RosterEntry> entries = new(_players.Count);
            foreach (KeyValuePair<PlayerId, string> player in _players)
            {
                if (player.Key == exclude) continue;

                entries.Add(new RosterEntry(player.Key, player.Value));
            }

            return new RosterSnapshotMessage { Players = entries.ToArray() };
        }

        /// <summary>
        /// Sends to one player by the id the host assigned them. Host only, because the peer-to-player
        /// table is the host's alone - a client sees the relay's virtual ids and cannot translate.
        /// </summary>
        /// <returns>Whether that player was found and reachable. False is worth acting on: it means a
        /// message addressed to somebody who has left, or who left between the send and this call.</returns>
        private static bool TrySendToPlayer<T>(T message, PlayerId player, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            int peerId = -1;
            foreach (KeyValuePair<int, PlayerId> mapping in _peerToPlayer)
            {
                if (mapping.Value != player) continue;

                peerId = mapping.Key;
                break;
            }

            if (peerId < 0) return false;

            _peerBuffer.Clear();
            NetworkAccess.GetPeers(_peerBuffer);

            foreach (INetPeer candidate in _peerBuffer)
            {
                if (candidate.Id != peerId) continue;

                NetworkAccess.Send(candidate, message, delivery);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Sends to every connected peer but one. The API has no such primitive and, through a relay, its
        /// broadcast carries no exclusion list - so the host unicasts instead. That costs one packet per
        /// client rather than one per session, which at two to four players is not worth an API and relay
        /// protocol change to avoid.
        /// </summary>
        private static void SendToClientsExcept<T>(T message, int exceptPeerId, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            _peerBuffer.Clear();
            NetworkAccess.GetPeers(_peerBuffer);

            foreach (INetPeer other in _peerBuffer)
            {
                if (other.Id == exceptPeerId) continue;

                NetworkAccess.Send(other, message, delivery);
            }
        }

        /// <summary>
        /// Makes a name from a stranger safe to print. The length cap is the message's job; this is about
        /// content, because a name is free text that lands in the log, and a newline in it lets the sender
        /// forge whole log lines.
        /// </summary>
        private static string Sanitize(string? name, PlayerId id)
        {
            if (string.IsNullOrWhiteSpace(name)) return id.ToString();

            char[] cleaned = name!.ToCharArray();
            for (int i = 0; i < cleaned.Length; i++)
            {
                if (char.IsControl(cleaned[i]))
                {
                    cleaned[i] = ' ';
                }
            }

            string result = new string(cleaned).Trim();
            return result.Length == 0 ? id.ToString() : result;
        }

        private static void Refuse(INetPeer peer, HandshakeRefusal refusal, string why)
        {
            HandshakeResultMessage result = new()
            {
                Accepted = false,
                HostProtocolVersion = ProtocolVersion,
                AssignedPlayerId = PlayerId.None,
                Refusal = refusal,
            };

            NetworkAccess.Send(peer, result);
            MelonLogger.Warning($"[Coop] Refused peer {peer.Id}: {why}.");
            peer.Disconnect();
        }

        private static string Describe(HandshakeResultMessage message)
        {
            return message.Refusal switch
            {
                HandshakeRefusal.SessionFull => "That session is full.",
                HandshakeRefusal.ProtocolMismatch =>
                    $"The host runs coop protocol {message.HostProtocolVersion}, this mod runs {ProtocolVersion}. Same version on both sides is required.",
                _ => "The host refused the connection without saying why.",
            };
        }

        private static string Describe(ConnectionRejection rejection)
        {
            return rejection switch
            {
                ConnectionRejection.SessionNotFound => "No session with that code. Check the code, or the host may have left.",
                ConnectionRejection.WrongPassword => "Wrong session password.",
                ConnectionRejection.SessionFull => "That session is full.",
                ConnectionRejection.DuplicateHost => "Somebody else already hosts under that code. Let the relay pick one instead.",
                ConnectionRejection.RelayFull => "The relay is full. Try again shortly or use another relay.",
                ConnectionRejection.RateLimited => "The relay is refusing connections from here for a moment. Wait and try again.",
                ConnectionRejection.TooManySessions => "This address already hosts as many sessions as the relay allows.",
                ConnectionRejection.TooManyAttempts => "The session stopped accepting joins after repeated wrong passwords.",
                ConnectionRejection.TooManyClients => "This address is already in as many sessions as the relay allows.",
                ConnectionRejection.PasswordNotRequired => "That session has no password. Clear the password field and join again.",
                ConnectionRejection.BadSessionCode => "That session code is not valid for this relay.",
                ConnectionRejection.BadHandshake => "The relay speaks a different protocol version. Update the mod or the relay.",
                _ => "The connection was refused.",
            };
        }
    }
}
