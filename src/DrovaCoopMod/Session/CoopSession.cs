using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using Il2Cpp;
using DrovaCoopMod.Messages;
using MelonLoader;

namespace DrovaCoopMod.Session
{
    /// <summary>
    /// The session layer: transport events to UI state, the roster, the version handshake and player identity.
    /// Runs on the Unity main thread, where the transport delivers.
    /// </summary>
    public static class CoopSession
    {
        /// <summary>The coop protocol version, defined in the protocol assembly so the headless peer and tests share it.</summary>
        public const ushort ProtocolVersion = CoopProtocol.Version;

        /// <summary>
        /// How long a join may sit in <see cref="CoopSessionState.Connecting"/>. A host without the mod accepts
        /// the connection and never answers the handshake.
        /// </summary>
        public const float ConnectTimeoutSeconds = 15f;

        private const float PingIntervalSeconds = 2f;

        /// <summary>A later reply would measure the timeout, not the link.</summary>
        private const float PingExpirySeconds = 10f;

        /// <summary>Unapplied blows held before the oldest is dropped, so a reload does not deliver a backlog at once.</summary>
        private const int MaxPendingDamage = 32;

        /// <summary>Unlearned things held before further ones are refused. Nothing restates them, so none may be dropped.</summary>
        private const int MaxPendingKnowledge = 256;

        /// <summary>Unpaid kills held before further ones are refused. Nothing restates a payout.</summary>
        private const int MaxPendingExperience = 512;

        /// <summary>
        /// Unapplied deaths held before further ones are refused. A lost death leaves a creature alive on one
        /// screen and in one save.
        /// </summary>
        private const int MaxPendingEnemyDeaths = 512;

        /// <summary>High because a join snapshot delivers a whole playthrough's moved variables at once.</summary>
        private const int MaxPendingGVarChanges = 4096;

        /// <summary>Sized to the variable database (~6200), since a late-game catch-up arrives all at once.</summary>
        private const int MaxPendingGVarCatchup = 16384;

        private static readonly Dictionary<PlayerId, string> PlayerNames = new();
        private static readonly Dictionary<PlayerId, PlayerStateMessage> StateByPlayer = new();
        private static readonly Dictionary<PlayerId, EquipmentSnapshot> EquipmentByPlayer = new();

        private static readonly Dictionary<PlayerId, (string[] Guids, int Version)> TalentsByPlayer = new();

        private static readonly Dictionary<PlayerId, PlayerVitalsMessage> VitalsByPlayer = new();
        private static readonly Dictionary<PlayerId, ItemUseSnapshot> ItemUsesByPlayer = new();

        // Host only. A transport peer id means nothing on another machine.
        private static readonly Dictionary<int, PlayerId> PeerToPlayer = new();

        private static readonly List<INetPeer> PeerBuffer = new();

        private static readonly Dictionary<PlayerId, float> RoundTripByPlayer = new();
        private static readonly Dictionary<uint, double> PingSentAt = new();

        // The highest blow already counted from each attacker makes a repeated report harmless.
        private static readonly Queue<DamageReportMessage> IncomingDamage = new();
        private static readonly Dictionary<PlayerId, uint> LastDamageSequence = new();

        // Applying an effect twice is a second stack of poison, not a harmless repeat.
        private static readonly Queue<StatusEffectMessage> IncomingEffects = new();
        private static readonly Dictionary<PlayerId, byte> LastStatusSequence = new();

        // No dedup: the game refuses a recipe it already has.
        private static readonly Queue<KnowledgeMessage> IncomingKnowledge = new();

        // Dedup lives in ExperienceShare, which also refuses payouts this machine made itself.
        private static readonly Queue<ExperienceMessage> IncomingExperience = new();

        // Held, not applied on arrival, because a conjure can carry a delay and the body may not exist yet.
        private static readonly Queue<SummonBindMessage> IncomingSummonBinds = new();

        // The set dedups both ways: the host relays, so a third player's copy comes back around.
        private static readonly Queue<EnemyDeathMessage> IncomingEnemyDeaths = new();
        private static readonly HashSet<string> DeadEnemies = new(StringComparer.Ordinal);

        // The claimed set itself is EnemyRegistry.ClaimedIds.
        // Claims, releases and drive changes in arrival order: a release and a re-claim of one id must not swap.
        private static readonly Queue<CreatureOrder> IncomingCreatureOrders = new();
        private static readonly Queue<EnemyGraveyardMessage> IncomingGraveyard = new();
        private static readonly Queue<EnemyAliveMessage> IncomingAlive = new();

        private static readonly Queue<EnemyReviveMessage> IncomingRevive = new();

        private static readonly Queue<GVarChangeMessage> IncomingGVars = new();

        private static readonly Queue<GVarChangeMessage> IncomingGVarCatchup = new();

        private static readonly Queue<ResourceWorkMessage> IncomingResourceWork = new();

        private static readonly Queue<ResourceSpotSnapshotMessage> IncomingResourceSnapshot = new();

        private const int MaxPendingLootSnapshots = 4096;

        private static readonly Queue<LootSnapshotMessage> IncomingLootSnapshot = new();

        private static readonly Queue<HaulPileMessage> IncomingHaulPiles = new();

        /// <summary>Whether the catch-up in flight replays consequences even for unchanged values.</summary>
        private static bool _resyncReplaysUnchanged;

        private static readonly Queue<CutsceneMessage> IncomingCutscenes = new();
        private static readonly Queue<ActorPortMessage> IncomingActorPorts = new();

        private static readonly Queue<WeatherMessage> IncomingWeather = new();

        private static readonly Queue<JournalEntryMessage> IncomingJournal = new();

        private static readonly Queue<JournalTopicMessage> IncomingJournalTopics = new();

        private static readonly Queue<NpcPlacementAnswerMessage> IncomingNpcPlacements = new();

        private static readonly Queue<DaytimeMessage> IncomingDaytime = new();

        private static readonly Queue<MapUnlockMessage> IncomingMapUnlocks = new();

        private static readonly Queue<DialogueExpMessage> IncomingDialogueExp = new();
        private static readonly Queue<DialogueItemsMessage> IncomingDialogueItems = new();
        private static readonly Queue<EnemyHealthMessage> IncomingEnemyHealth = new();
        private static readonly Queue<EnemySnapshotMessage> IncomingSnapshots = new();
        private static readonly Queue<EnemySpawnMessage> IncomingSpawns = new();

        // Host only.
        private static readonly Queue<EnemyDamageMessage> IncomingEnemyDamage = new();
        private static readonly Dictionary<PlayerId, uint> LastEnemyDamageSequence = new();
        private static readonly Queue<EnemyEngageMessage> IncomingEnemyEngage = new();
        private static readonly Queue<EnemyEngageMessage> IncomingHostEngage = new();
        private static readonly Queue<EnemyDamageMessage> IncomingLeasedDamage = new();

        // Host only: clients asking for the day to turn over because they slept.
        private static readonly Queue<DayAdvanceRequestMessage> IncomingDayAdvance = new();
        private static uint _nextEnemyDamageSequence;
        private static uint _lastLeasedDamageSequence;

        private static readonly List<string> GraveyardChunk = new();
        private static readonly List<string> AliveChunk = new();

        // A real clock: summed frame deltas quantize a round trip to whole frames.
        private static readonly System.Diagnostics.Stopwatch Clock = new();

        private static readonly List<Action> Unregisters = new();

        private static bool _initialized;
        private static float _connectingSeconds;
        private static float _sincePing;
        private static uint _nextPingToken;
        private static uint _nextDamageSequence;

        // Per target: the receiver's half-range window would read a shared counter's jumps as late and drop them.
        private static readonly Dictionary<PlayerId, byte> NextStatusSequence = new();

        /// <summary>
        /// Where the local player is in getting into a session. The setter announces every transition through
        /// <c>CoopAccess.OnSessionChanged</c>.
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

        internal static bool IsLive => State is CoopSessionState.Hosting or CoopSessionState.Joined;

        /// <summary>
        /// <see cref="PlayerId.Host"/> when hosting, the host-assigned id when joined, <see cref="PlayerId.None"/> outside a session.
        /// </summary>
        public static PlayerId LocalPlayerId { get; private set; } = PlayerId.None;

        /// <summary>
        /// Whether players may damage each other. Always the host's setting, so one player is never hittable by
        /// someone they cannot hit back. Can change mid-session, so readers must keep watching it.
        /// </summary>
        public static bool FriendlyFire { get; private set; }

        /// <summary>
        /// Health a downed player stands up with, as a percentage of maximum. The host's setting, falling back to
        /// <see cref="CoopProtocol.DefaultRecoveryHealthPercent"/>.
        /// </summary>
        public static byte RecoveryHealthPercent { get; private set; } = CoopProtocol.DefaultRecoveryHealthPercent;

        /// <summary>
        /// Creature toughness as a percentage of authored health. The host's setting, so a blow scales the same
        /// on every machine. See <see cref="World.EnemyScaling"/>.
        /// </summary>
        public static ushort EnemyHealthPercent { get; private set; } = CoopProtocol.DefaultEnemyHealthPercent;

        /// <summary>The host's loot rule. See <see cref="World.LootShare"/>.</summary>
        public static byte LootMode { get; private set; } = CoopProtocol.LootModeInstanced;

        /// <summary>Why the last attempt failed, for the player. Null unless <see cref="State"/> is <see cref="CoopSessionState.Failed"/>.</summary>
        public static string? LastError { get; private set; }

        /// <summary>State updates refused for NaN or absurd numbers. Nonzero means a peer is sending nonsense.</summary>
        public static int ImplausibleStates { get; private set; }

        /// <summary>The code others need to join a relay session. Null for a direct session.</summary>
        public static string? SessionCode => NetworkAccess.SessionCode;

        /// <summary>Whether the traffic is sealed against the relay operator.</summary>
        public static bool IsEncrypted => NetworkAccess.IsEncrypted;

        /// <summary>Everyone in the session, including the local player, keyed by host-assigned id.</summary>
        public static IReadOnlyDictionary<PlayerId, string> Players => PlayerNames;

        /// <summary>The most recent state each player sent.</summary>
        public static IReadOnlyDictionary<PlayerId, PlayerStateMessage> LatestStates => StateByPlayer;

        /// <summary>Each other player's equipped item ids, versioned because applying equipment rebuilds the body's slots.</summary>
        public static IReadOnlyDictionary<PlayerId, EquipmentSnapshot> Equipment => EquipmentByPlayer;

        /// <summary>Each other player's learned talents, with a version that counts their changes.</summary>
        public static IReadOnlyDictionary<PlayerId, (string[] Guids, int Version)> Talents => TalentsByPlayer;

        /// <summary>Each far player's bar maximums. Absent until their first <see cref="PlayerVitalsMessage"/>.</summary>
        public static IReadOnlyDictionary<PlayerId, PlayerVitalsMessage> Vitals => VitalsByPlayer;

        /// <summary>The last ability each other player announced. Only the newest is kept, so bodies never work through a backlog.</summary>
        public static IReadOnlyDictionary<PlayerId, ItemUseSnapshot> ItemUses => ItemUsesByPlayer;

        /// <summary>Registers the message types and subscribes to the transport. Only the first call does anything.</summary>
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
                if (m.IsPlausible()) QueueFromHost(m, IncomingSummonBinds, MaxPendingEnemyDeaths, "conjured creatures");
            });
            Register<EnemyDeathMessage>(MessageIds.EnemyDeath, OnEnemyDeath);
            Register<EnemyBindMessage>(MessageIds.EnemyBind,
                (peer, m) => QueueFromHost(new CreatureOrder(m), IncomingCreatureOrders, MaxPendingEnemyDeaths, "creature claims"));
            Register<EnemyReleaseMessage>(MessageIds.EnemyRelease,
                (peer, m) => QueueFromHost(new CreatureOrder(m), IncomingCreatureOrders, MaxPendingEnemyDeaths, "creature releases"));
            Register<EnemyDamageMessage>(MessageIds.EnemyDamage, OnEnemyDamage);
            Register<EnemyEngageMessage>(MessageIds.EnemyEngage, OnEnemyEngage);
            Register<DayAdvanceRequestMessage>(MessageIds.DayAdvanceRequest, OnDayAdvanceRequest);
            Register<EnemyHealthMessage>(MessageIds.EnemyHealth, OnEnemyHealth);
            Register<EnemyGraveyardMessage>(MessageIds.EnemyGraveyard,
                (peer, m) => QueueFromHost(m, IncomingGraveyard, MaxPendingEnemyDeaths, "graveyard chunks"));
            Register<EnemyAliveMessage>(MessageIds.EnemyAlive,
                (peer, m) => QueueFromHost(m, IncomingAlive, MaxPendingEnemyDeaths, "alive answers"));
            Register<EnemyReviveMessage>(MessageIds.EnemyRevive,
                (peer, m) => QueueFromHost(m, IncomingRevive, MaxPendingEnemyDeaths, "revive decrees"));

            // Never relayed back: a world reacting to its own story twice tells it twice.
            Register<GVarChangeMessage>(MessageIds.GVarChange,
                (peer, m) => RelayAndQueue(peer, m, IncomingGVars, MaxPendingGVarChanges, "gvar changes"));
            Register<GVarSnapshotMessage>(MessageIds.GVarSnapshot, OnGVarSnapshot);
            Register<GVarSnapshotEndMessage>(MessageIds.GVarSnapshotEnd, OnGVarSnapshotEnd);
            Register<GVarResyncRequestMessage>(MessageIds.GVarResyncRequest, OnGVarResyncRequest);

            // Stamped with who actually swung, because a client cannot be trusted to name itself.
            Register<ResourceWorkMessage>(MessageIds.ResourceWork, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting && PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) m.Player = sender.Value;

                RelayAndQueue(peer, m, IncomingResourceWork, MaxPendingEnemyDeaths, "resource work");
            });
            Register<ResourceSpotSnapshotMessage>(MessageIds.ResourceSpotSnapshot, (peer, m) =>
            {
                if (m.Records != null) QueueFromHost(m, IncomingResourceSnapshot, MaxPendingEnemyDeaths, "resource spot records");
            });
            Register<ResourceSpotSnapshotEndMessage>(MessageIds.ResourceSpotSnapshotEnd, OnResourceSpotSnapshotEnd);

            // Stamped with who actually hauled it, for the same reason resource work is.
            Register<HaulPileMessage>(MessageIds.HaulPile, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting && PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) m.Player = sender.Value;

                RelayAndQueue(peer, m, IncomingHaulPiles, MaxPendingEnemyDeaths, "hauled piles");
            });

            // Never relayed back to the sender, because replaying a cutscene twice is not a no-op.
            Register<CutsceneMessage>(MessageIds.Cutscene,
                (peer, m) => RelayAndQueue(peer, m, IncomingCutscenes, MaxPendingEnemyDeaths, "cutscene starts"));
            Register<ActorPortMessage>(MessageIds.ActorPort,
                (peer, m) => RelayAndQueue(peer, m, IncomingActorPorts, MaxPendingEnemyDeaths, "cutscene actor ports"));
            Register<WeatherMessage>(MessageIds.Weather, OnWeather);

            // The sender is excluded only because the echo is noise.
            Register<JournalEntryMessage>(MessageIds.JournalEntry,
                (peer, m) => RelayAndQueue(peer, m, IncomingJournal, MaxPendingEnemyDeaths, "journal entries"));
            Register<JournalTopicMessage>(MessageIds.JournalTopic,
                (peer, m) => RelayAndQueue(peer, m, IncomingJournalTopics, MaxPendingEnemyDeaths, "completion marks"));
            Register<NpcPlacementRequestMessage>(MessageIds.NpcPlacementRequest, OnNpcPlacementRequest);
            Register<NpcPlacementAnswerMessage>(MessageIds.NpcPlacementAnswer,
                (peer, m) => QueueFromHost(m, IncomingNpcPlacements, MaxPendingEnemyDeaths, "placement answers"));
            Register<DaytimeMessage>(MessageIds.Daytime, OnDaytime);
            Register<MapUnlockMessage>(MessageIds.MapUnlock,
                (peer, m) => RelayAndQueue(peer, m, IncomingMapUnlocks, MaxPendingEnemyDeaths, "map unlocks"));

            // Never back to the sender: a payment is not idempotent.
            Register<DialogueExpMessage>(MessageIds.DialogueExp,
                (peer, m) => RelayAndQueue(peer, m, IncomingDialogueExp, MaxPendingEnemyDeaths, "dialogue rewards"));
            Register<DialogueItemsMessage>(MessageIds.DialogueItems,
                (peer, m) => RelayAndQueue(peer, m, IncomingDialogueItems, MaxPendingEnemyDeaths, "dialogue items"));
            Register<EnemySnapshotMessage>(MessageIds.EnemySnapshot, OnEnemySnapshot);
            Register<WorldStateRequestMessage>(MessageIds.WorldStateRequest, OnWorldStateRequest);
            Register<EnemySpawnMessage>(MessageIds.EnemySpawn,
                (peer, m) => QueueFromHost(m, IncomingSpawns, MaxPendingEnemyDeaths, "spawned creatures"));
            Register<EnemyDriveMessage>(MessageIds.EnemyDrive,
                (peer, m) => QueueFromHost(new CreatureOrder(m), IncomingCreatureOrders, MaxPendingEnemyDeaths, "creature drive changes"));
            Register<LootTakenMessage>(MessageIds.LootTaken, OnLootTaken);
            Register<LootSnapshotMessage>(MessageIds.LootSnapshot,
                (peer, m) => QueueFromHost(m, IncomingLootSnapshot, MaxPendingLootSnapshots, "loot the session had already seen go"));
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
                    if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
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
                    if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
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
            Register<HostSettingMessage>(MessageIds.HostSetting, (peer, m) =>
            {
                if (IsFromHost()) HostSettings.Take(m);
            });
            Register<ThunderMessage>(MessageIds.Thunder, (peer, m) =>
            {
                if (IsFromHost()) World.WeatherSync.TakeThunder();
            });
            Register<RegionMessage>(MessageIds.Region, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting && PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) World.RegionShare.Take(sender, m);
            });
            Register<LeaseSummonMessage>(MessageIds.LeaseSummon, (peer, m) =>
            {
                if (State == CoopSessionState.Hosting && PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) World.LeaseSummons.Take(sender, m);
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

        /// <summary>Records the matching unregister for <see cref="Shutdown"/>.</summary>
        private static void Register<T>(ushort id, Action<INetPeer, T> handler) where T : struct, INetMessage
        {
            NetworkAccess.RegisterWithId(id, handler);
            Unregisters.Add(NetworkAccess.Unregister<T>);
        }

        /// <summary>The host queues a client's message and relays it to everyone but the sender. A client queues only the host's.</summary>
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

        /// <summary>A message only the host may send.</summary>
        private static void QueueFromHost<T>(T message, Queue<T> queue, int cap, string what) where T : struct
        {
            if (!IsFromHost()) return;

            Enqueue(queue, message, cap, what);
        }

        /// <summary>Hosts through a relay. A null code lets the relay pick one, which nobody can claim by guessing first.</summary>
        public static void HostViaRelay(string relayAddress, int relayPort, string? sessionCode, string? password)
        {
            if (!Begin()) return;

            MelonLogger.Msg($"[Coop] Hosting through {relayAddress}:{relayPort}...");
            NetworkAccess.StartHostViaRelay(relayAddress, relayPort, sessionCode, password);
        }

        /// <summary>Joins a relay session by its code.</summary>
        public static void JoinViaRelay(string relayAddress, int relayPort, string sessionCode, string? password)
        {
            if (!Begin()) return;

            MelonLogger.Msg($"[Coop] Joining '{sessionCode}' through {relayAddress}:{relayPort}...");
            NetworkAccess.ConnectViaRelay(relayAddress, relayPort, sessionCode, password);
        }

        /// <summary>Leaves any session and returns to <see cref="CoopSessionState.Idle"/>, forgetting the last failure.</summary>
        public static void Leave()
        {
            Teardown(announce: true);

            LastError = null;
            State = CoopSessionState.Idle;
        }

        /// <summary>Advances the session's timers. Call once per frame.</summary>
        /// <param name="unscaledDeltaTime">Unscaled, because the transport keeps running while a menu pauses the world.</param>
        public static void Tick(float unscaledDeltaTime)
        {
            if (IsLive)
            {
                // The relay dropped the transport. Without this the host keeps "hosting" a dead connection.
                if (NetworkAccess.Role == NetRole.None)
                {
                    Fail("The connection to the relay was lost. Host again to continue; teammates join with the new code.");
                    return;
                }

                TickRules();
                TickPing(unscaledDeltaTime);
                HostSettings.Tick(unscaledDeltaTime);
                return;
            }

            if (State != CoopSessionState.Connecting) return;

            _connectingSeconds += unscaledDeltaTime;
            if (_connectingSeconds < ConnectTimeoutSeconds) return;

            Fail($"No answer from the host within {ConnectTimeoutSeconds:0} seconds. It may be running a different mod set, or none.");
        }

        /// <summary>The round trip to another player in milliseconds, or null before one has been measured.</summary>
        public static float? RoundTripMs(PlayerId player)
        {
            return RoundTripByPlayer.TryGetValue(player, out float value) ? value : null;
        }

        private static void TickPing(float unscaledDeltaTime)
        {
            _sincePing += unscaledDeltaTime;
            if (_sincePing < PingIntervalSeconds) return;

            _sincePing = 0f;

            if (!Clock.IsRunning)
            {
                Clock.Restart();
            }

            // Pings go out unreliable, so unanswered tokens are ordinary and must expire.
            double now = Clock.Elapsed.TotalMilliseconds;

            foreach (KeyValuePair<uint, double> pending in PingSentAt)
            {
                if (now - pending.Value > PingExpirySeconds * 1000d)
                {
                    PingSentAt.Remove(pending.Key);
                }
            }

            uint token = unchecked(++_nextPingToken);
            PingSentAt[token] = now;

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
                // The token belongs to the sender and is echoed back uninterpreted.
                PingMessage reply = new()
                {
                    Sender = LocalPlayerId,
                    Token = message.Token,
                    IsReply = true,
                };

                NetworkAccess.Send(peer, reply, Delivery.Unreliable);
                return;
            }

            if (!PingSentAt.Remove(message.Token, out double sentAt)) return;

            // Attributed by the connection, not the id the reply carries.
            PlayerId sender;
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!PeerToPlayer.TryGetValue(peer.Id, out sender)) return;
            }
            else
            {
                sender = PlayerId.Host;
            }

            RoundTripByPlayer[sender] = (float)(Clock.Elapsed.TotalMilliseconds - sentAt);
        }

        /// <summary>
        /// Drops every transport subscription and message registration. Both are static, so a hot reload would
        /// otherwise handle every message twice.
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

            foreach (Action unregister in Unregisters)
            {
                unregister();
            }

            Unregisters.Clear();

            _initialized = false;
        }

        /// <summary>
        /// Sends the local player's state to everyone. Sequenced so an overtaken datagram cannot put a stale
        /// stance back on the far copy.
        /// </summary>
        /// <param name="message">The caller fills in the intent. This stamps the id.</param>
        public static void BroadcastPlayerState(PlayerStateMessage message)
        {
            if (!IsLive) return;

            message.PlayerId = LocalPlayerId;
            NetworkAccess.SendToAll(message, Delivery.Sequenced);
        }

        /// <summary>Sends the local player's equipment. Reliable, since it is sent only on change and a lost one stays wrong.</summary>
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
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                // Relayed part by part: every receiver gathers the parts itself.
                message.PlayerId = sender;
                SendToClientsExcept(message, peer.Id);

                if (Gather(sender, ref message)) Remember(sender, message);
                return;
            }

            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            if (Gather(message.PlayerId, ref message)) Remember(message.PlayerId, message);
        }

        /// <summary>Tells everyone which talents the local player has learned.</summary>
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
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                // Relayed part by part: every receiver gathers the parts itself.
                message.PlayerId = sender;
                SendToClientsExcept(message, peer.Id);

                if (Gather(sender, ref message)) RememberTalents(sender, message.Guids);
                return;
            }

            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            if (Gather(message.PlayerId, ref message)) RememberTalents(message.PlayerId, message.Guids);
        }

        // Lists that crossed in parts, held until the last part. Receivers replace what they knew, so acting
        // on each part would leave only the last one.
        private static readonly Dictionary<PlayerId, PlayerEquipmentMessage> EquipmentParts = new();
        private static readonly Dictionary<PlayerId, string[]> TalentParts = new();
        private static readonly Dictionary<(int Peer, string Key), LootStack[]> LootParts = new();

        // Bounds what a peer can make this machine hold. Past it the parts are dropped.
        private const int MaxGathered = 255;

        /// <returns>True when the message is now the whole outfit.</returns>
        private static bool Gather(PlayerId player, ref PlayerEquipmentMessage message)
        {
            if (EquipmentParts.Remove(player, out PlayerEquipmentMessage earlier))
            {
                message.ItemIds = [.. earlier.ItemIds, .. message.ItemIds];
                message.ItemSets = [.. earlier.ItemSets, .. message.ItemSets];
            }

            if (!message.More) return true;

            if (message.ItemIds.Length <= MaxGathered) EquipmentParts[player] = message;
            return false;
        }

        /// <returns>True when the message is now the whole list.</returns>
        private static bool Gather(PlayerId player, ref PlayerTalentsMessage message)
        {
            if (TalentParts.Remove(player, out string[]? earlier)) message.Guids = [.. earlier, .. message.Guids];

            if (!message.More) return true;

            if (message.Guids.Length <= MaxGathered) TalentParts[player] = message.Guids;
            return false;
        }

        /// <returns>True when the message is now the whole container.</returns>
        private static bool Gather(INetPeer peer, ref LootTakenMessage message)
        {
            (int, string) key = (peer.Id, message.RootKey);
            if (LootParts.Remove(key, out LootStack[]? earlier)) message.Remaining = [.. earlier, .. message.Remaining];

            if (!message.More) return true;

            if (message.Remaining.Length <= MaxGathered) LootParts[key] = message.Remaining;
            return false;
        }

        private static void RememberTalents(PlayerId player, string[] guids)
        {
            int version = TalentsByPlayer.TryGetValue(player, out (string[] Guids, int Version) existing) ? existing.Version + 1 : 1;
            TalentsByPlayer[player] = (guids, version);
        }

        // Versioned so a body knows it has something new to put on.
        private static void Remember(PlayerId player, in PlayerEquipmentMessage message)
        {
            int version = EquipmentByPlayer.TryGetValue(player, out EquipmentSnapshot existing) ? existing.Version + 1 : 1;
            EquipmentByPlayer[player] = new EquipmentSnapshot(
                message.ItemIds ?? [],
                message.ItemSets ?? [],
                message.ActiveSet,
                message.BodyType,
                message.VoiceId,
                version);
        }

        /// <summary>Tells everyone the local player started or stopped using an ability. Reliable, since it is sent only on cast edges.</summary>
        /// <param name="abilityId">The readable id of the ability item.</param>
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
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.User = sender;
                Remember(sender, message);

                SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!IsFromHost()) return;
            if (message.User == LocalPlayerId) return;

            Remember(message.User, message);
        }

        // Versioned because casting the same spell twice is two casts.
        private static void Remember(PlayerId player, in ItemUseMessage message)
        {
            int version = ItemUsesByPlayer.TryGetValue(player, out ItemUseSnapshot existing) ? existing.Version + 1 : 1;
            ItemUsesByPlayer[player] = new ItemUseSnapshot(message.ItemId ?? string.Empty, message.Sequence, message.Released != 0, version);
        }

        /// <summary>
        /// Tells another player the local player hit them. The figure is before their defense, since the victim's
        /// game resists it. See <see cref="DamageReportMessage"/>.
        /// </summary>
        public static void ReportDamage(PlayerId victim, int amount, float poiseDamage, bool appliesBleed,
            byte damageType, byte origin = DamageReportMessage.OriginPlayer)
        {
            if (!IsLive) return;

            // Friendly fire is checked here too, for the frame the host turned it off. Creature blows only reach
            // the victim this way, so they are never refused.
            if (origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(victim)) return;

            if (!victim.IsAssigned || victim == LocalPlayerId) return;
            if (amount <= 0) return;

            // Capped rather than wrapped.
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

                // A NaN would reach the far side as arithmetic on their stance.
                PoiseDamage = poiseDamage > 0f && !float.IsNaN(poiseDamage) && !float.IsInfinity(poiseDamage) ? poiseDamage : 0f,
                AppliesBleed = appliesBleed ? (byte)1 : (byte)0,
                DamageType = damageType,
                Origin = origin,
            };

            // Remembered first so the answering verdict is recognized. Only a player's blow is answered.
            if (origin == DamageReportMessage.OriginPlayer) World.BlockVerdicts.Await(victim, report.Sequence);

            if (NetworkAccess.Role == NetRole.Host)
            {
                TrySendToPlayer(report, victim);
                return;
            }

            // A client's only peer is the host, and peer ids are machine-local, so the host addresses the victim.
            NetworkAccess.SendToAll(report);
        }

        /// <summary>Takes the next blow for the local player, oldest first. Queued because there may be no body yet.</summary>
        public static bool TryTakeDamage(out DamageReportMessage report)
            => IncomingDamage.TryDequeue(out report);

        private static void OnDamageReport(INetPeer peer, DamageReportMessage message)
        {
            if (!IsLive) return;

            if (message.Amount == 0) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                // The sender does not get to say who swung.
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Attacker = sender;

                if (message.Origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(sender, message.Victim)) return;

                // A player's own swings land on their own machine, so a self-report is a bug or an exploit.
                if (message.Victim == sender) return;

                if (message.Victim == LocalPlayerId)
                {
                    Enqueue(message);
                    return;
                }

                if (!PlayerNames.ContainsKey(message.Victim)) return;

                TrySendToPlayer(message, message.Victim);
                return;
            }

            // Only the host sends these, and only blows addressed to this player.
            if (!IsFromHost()) return;
            if (message.Victim != LocalPlayerId) return;
            if (message.Origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(message.Attacker)) return;

            Enqueue(message);
        }

        private static void Enqueue(in DamageReportMessage report)
        {
            // Reliable delivery makes a duplicate unlikely, not impossible, and the transport does not reject replays.
            if (LastDamageSequence.TryGetValue(report.Attacker, out uint last) && report.Sequence <= last) return;

            LastDamageSequence[report.Attacker] = report.Sequence;

            EnqueueLatest(IncomingDamage, report, MaxPendingDamage);
        }

        /// <summary>Tells everyone the local player learned something, like a recipe whose paper is gone afterwards.</summary>
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

        /// <summary>Takes the next thing learned elsewhere that this player has not been taught yet.</summary>
        public static bool TryTakeKnowledge(out KnowledgeMessage message)
            => IncomingKnowledge.TryDequeue(out message);

        private static void OnKnowledge(INetPeer peer, KnowledgeMessage message)
        {
            if (!IsLive) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Learner = sender;

                // Relayed first so a third player does not wait for the host's world to be ready.
                SendToClientsExcept(message, peer.Id);
            }
            else if (!IsFromHost() || message.Learner == LocalPlayerId)
            {
                return;
            }

            // Refused, never dropping the oldest: nothing repeats a recipe. The cap only bounds a hostile peer.
            if (!Enqueue(IncomingKnowledge, message, MaxPendingKnowledge))
            {
                MelonLogger.Warning($"[Know] more than {MaxPendingKnowledge} things are waiting to be learned; " +
                    $"'{message.Id}' from {message.Learner} was refused and is lost.");
            }
        }

        /// <summary>Tells everyone what a creature was worth, so they can be paid for it too.</summary>
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

        /// <summary>Takes the next creature somebody else was paid for that this player has not been.</summary>
        public static bool TryTakeExperience(out ExperienceMessage message)
            => IncomingExperience.TryDequeue(out message);

        private static void OnExperience(INetPeer peer, ExperienceMessage message)
        {
            if (!IsLive) return;
            if (!message.IsPlausible()) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Earner = sender;

                SendToClientsExcept(message, peer.Id);
            }
            else if (!IsFromHost() || message.Earner == LocalPlayerId)
            {
                return;
            }

            // Refused rather than dropping the oldest: nothing restates a payout.
            if (!Enqueue(IncomingExperience, message, MaxPendingExperience))
            {
                MelonLogger.Warning($"[Exp] more than {MaxPendingExperience} kills are waiting to be paid out; " +
                    $"{message.NetId} from {message.Earner} was refused and that experience is lost.");
            }
        }

        /// <summary>
        /// Tells everyone a non-player died here. Refused when already reported or applied, which stops the relay
        /// echoing it back with three players.
        /// </summary>
        public static void ReportEnemyDeath(string actorGuid, bool isReconcile)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(actorGuid)) return;

            if (!DeadEnemies.Add(actorGuid)) return;

            NetworkAccess.SendToAll(new EnemyDeathMessage
            {
                ActorGuid = actorGuid,
                Flags = isReconcile ? EnemyDeathMessage.FlagReconcile : (byte)0,
            });
        }

        /// <summary>Takes the next death that has arrived and not been applied yet.</summary>
        public static bool TryTakeEnemyDeath(out EnemyDeathMessage message)
            => IncomingEnemyDeaths.TryDequeue(out message);

        /// <summary>How many creatures this session knows to be dead. For <c>coop_status</c>.</summary>
        public static int KnownDeadEnemies => DeadEnemies.Count;

        /// <summary>A savegame was loaded: the save says who is dead again.</summary>
        internal static void ForgetDeaths() => DeadEnemies.Clear();

        /// <summary>
        /// Takes a creature out of the dead set so later asks are answered from the host's decree. The other half
        /// of a revive is <see cref="World.EnemyRegistry.ForceAlive"/>.
        /// </summary>
        public static void ForgetEnemyDeath(string actorGuid)
        {
            if (string.IsNullOrEmpty(actorGuid)) return;

            DeadEnemies.Remove(actorGuid);
        }

        private static void OnEnemyDeath(INetPeer peer, EnemyDeathMessage message)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(message.ActorGuid)) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                // Refused before relaying, or the same death would circle the session once per report.
                if (!DeadEnemies.Add(message.ActorGuid)) return;

                // Relayed first so a third player does not wait for the host's world to have a body.
                SendToClientsExcept(message, peer.Id);
            }
            else if (!IsFromHost() || !DeadEnemies.Add(message.ActorGuid))
            {
                return;
            }

            // Refused rather than dropping the oldest: nothing repeats a death.
            if (!Enqueue(IncomingEnemyDeaths, message, MaxPendingEnemyDeaths))
            {
                MelonLogger.Warning($"[Enemy] more than {MaxPendingEnemyDeaths} deaths are waiting to be applied; " +
                    $"'{message.ActorGuid}' was refused and that creature will stay alive here.");
            }
        }

        /// <summary>Claims a creature and tells every client about it.</summary>
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

        /// <summary>Tells everyone which handle one conjured creature answers to.</summary>
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
        public static bool TryTakeSummonBind(out SummonBindMessage message)
            => IncomingSummonBinds.TryDequeue(out message);

        /// <summary>
        /// Tells every client whether the host moves one spawned creature's body. Same ordered channel as the
        /// spawn and summon bind, so it never overtakes them.
        /// </summary>
        public static void SendEnemyDrive(EnemyNetId id, bool hostDrives)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyDriveMessage
            {
                NetId = id,
                HostDrives = hostDrives,
            });
        }

        /// <summary>Gives a creature back and tells every client.</summary>
        public static void ReleaseEnemy(EnemyNetId id, byte reason)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyReleaseMessage
            {
                NetId = id,
                Reason = reason,
            });
        }

        /// <summary>Tells everyone what a creature's health is.</summary>
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

        /// <summary>Reports a blow the local player landed on a host-owned creature. Sequenced so a resend is harmless.</summary>
        public static void ReportEnemyDamage(EnemyNetId id, int amount, float poiseDamage, bool appliesBleed,
            byte damageType, byte flags = 0)
        {
            if (State != CoopSessionState.Joined) return;
            if (amount <= 0) return;

            NetworkAccess.SendToAll(BuildEnemyDamage(id, amount, poiseDamage, appliesBleed, damageType, flags));
        }

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
        /// Tells the host this player is in a dialogue or brawl with one of its NPCs, or that it ended. Repeated
        /// while it lasts, and the host expires the silent.
        /// </summary>
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

        /// <summary>Sends everything already dead to somebody who has just joined, in chunks.</summary>
        private static void SendGraveyard(INetPeer peer, IReadOnlyCollection<string> dead)
        {
            if (State != CoopSessionState.Hosting || peer == null) return;

            // The last chunk always goes, even empty, since a short chunk is what ends the list.
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
                Guids = [.. GraveyardChunk],
                More = more ? (byte)1 : (byte)0,
            });

            GraveyardChunk.Clear();
        }

        /// <summary>
        /// Sends where the host-driven creatures are. Sequenced, so a late batch is dropped instead of walking
        /// every creature back one interval.
        /// </summary>
        public static void BroadcastEnemySnapshot(in EnemySnapshotMessage message)
        {
            if (State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(message, Delivery.Sequenced);
        }

        /// <summary>Takes the next batch of creature positions that has arrived.</summary>
        public static bool TryTakeEnemySnapshot(out EnemySnapshotMessage message)
            => IncomingSnapshots.TryDequeue(out message);

        /// <summary>Sends the host this client's leased brawl fighters, so its held copies follow (LeaseMirror).</summary>
        public static void SendLeaseSnapshot(in EnemySnapshotMessage message)
        {
            if (State != CoopSessionState.Joined) return;

            NetworkAccess.SendToAll(message, Delivery.Sequenced);
        }

        /// <summary>Takes the newest batch a client sent about its leased fighters, and who sent it.</summary>
        public static bool TryTakeLeaseSnapshot(out PlayerId sender, out EnemySnapshotMessage message)
        {
            foreach (KeyValuePair<PlayerId, EnemySnapshotMessage> pending in IncomingLeaseSnapshots)
            {
                sender = pending.Key;
                message = pending.Value;
                IncomingLeaseSnapshots.Remove(sender);
                return true;
            }

            sender = default;
            message = default;
            return false;
        }

        // Newest per client.
        private static readonly Dictionary<PlayerId, EnemySnapshotMessage> IncomingLeaseSnapshots = new();

        private static void OnEnemySnapshot(INetPeer peer, EnemySnapshotMessage message)
        {
            // On the host, a client's leased fighters, attributed by connection.
            if (State == CoopSessionState.Hosting)
            {
                if (PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) IncomingLeaseSnapshots[sender] = message;
                return;
            }

            if (!IsFromHost()) return;

            // Only the newest: each supersedes the last, and a backlog would retrace old positions.
            // Never clear the spawn queue here, since a spawn is said once.
            IncomingSnapshots.Clear();
            IncomingSnapshots.Enqueue(message);
        }

        /// <summary>Asks the host which of these creatures its world has already killed.</summary>
        public static void AskAboutWorldState(List<string> guids)
        {
            if (State != CoopSessionState.Joined || guids == null || guids.Count == 0) return;

            NetworkAccess.SendToAll(new WorldStateRequestMessage { Guids = guids.ToArray() });
        }

        private static void OnWorldStateRequest(INetPeer peer, WorldStateRequestMessage message)
        {
            if (State != CoopSessionState.Hosting || message.Guids == null) return;

            GraveyardChunk.Clear();
            AliveChunk.Clear();

            foreach (string guid in message.Guids)
            {
                if (string.IsNullOrEmpty(guid)) continue;

                // Checked before the save, which keeps a revived creature dead until the next autosave.
                if (World.EnemyRegistry.IsKnownAlive(guid))
                {
                    AliveChunk.Add(guid);

                    if (AliveChunk.Count >= EnemyAliveMessage.MaxGuids)
                    {
                        SendAliveChunk(peer);
                    }

                    continue;
                }

                // The save counts too: a creature the host killed solo long ago was never mentioned on the wire.
                if (DeadEnemies.Contains(guid) || !SavegameDataExtensions.IsActorAlive(guid))
                {
                    GraveyardChunk.Add(guid);

                    if (GraveyardChunk.Count >= EnemyGraveyardMessage.MaxGuids)
                    {
                        SendGraveyardChunk(peer, more: true);
                    }

                    continue;
                }

                // Alive is said explicitly, since the client only asks about its own corpses.
                AliveChunk.Add(guid);

                if (AliveChunk.Count >= EnemyAliveMessage.MaxGuids)
                {
                    SendAliveChunk(peer);
                }
            }

            // Always sent: silence looks like a lost packet and the client would ask forever.
            SendGraveyardChunk(peer, more: false);

            if (AliveChunk.Count > 0)
            {
                SendAliveChunk(peer);
            }
        }

        private static void SendAliveChunk(INetPeer peer)
        {
            NetworkAccess.Send(peer, new EnemyAliveMessage
            {
                Guids = [.. AliveChunk],
            });

            AliveChunk.Clear();
        }

        /// <summary>Tells everyone about a creature this machine spawned at runtime.</summary>
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
        public static bool TryTakeEnemySpawn(out EnemySpawnMessage message)
            => IncomingSpawns.TryDequeue(out message);

        /// <summary>Takes the next claim, release or drive change, in the order the host sent them.</summary>
        internal static bool TryTakeCreatureOrder(out CreatureOrder order)
            => IncomingCreatureOrders.TryDequeue(out order);

        /// <summary>Takes the next graveyard chunk that has arrived.</summary>
        public static bool TryTakeGraveyard(out EnemyGraveyardMessage message)
            => IncomingGraveyard.TryDequeue(out message);

        /// <summary>Takes the next chunk of creatures the host has said are alive.</summary>
        public static bool TryTakeAlive(out EnemyAliveMessage message)
            => IncomingAlive.TryDequeue(out message);

        /// <summary>Takes the next chunk of creatures the host has revived by decree.</summary>
        public static bool TryTakeRevive(out EnemyReviveMessage message)
            => IncomingRevive.TryDequeue(out message);

        /// <summary>Takes the next live global-variable change that arrived.</summary>
        public static bool TryTakeGVarChange(out GVarChangeMessage message)
            => IncomingGVars.TryDequeue(out message);

        /// <summary>Takes the next join catch-up record. Applied as state only, never as an event.</summary>
        public static bool TryTakeGVarCatchup(out GVarChangeMessage message)
            => IncomingGVarCatchup.TryDequeue(out message);

        /// <summary>Takes the next shared cutscene start that arrived.</summary>
        public static bool TryTakeCutscene(out CutsceneMessage message)
            => IncomingCutscenes.TryDequeue(out message);

        /// <summary>Takes the next NPC port a teammate's cutscene made, if any.</summary>
        public static bool TryTakeActorPort(out ActorPortMessage message)
            => IncomingActorPorts.TryDequeue(out message);

        /// <summary>Takes the next weather report from the host.</summary>
        public static bool TryTakeWeather(out WeatherMessage message)
            => IncomingWeather.TryDequeue(out message);

        /// <summary>Takes the next batch of shared journal entries.</summary>
        public static bool TryTakeJournal(out JournalEntryMessage message)
            => IncomingJournal.TryDequeue(out message);

        /// <summary>Takes the next placement answer from the host.</summary>
        public static bool TryTakeNpcPlacement(out NpcPlacementAnswerMessage message)
            => IncomingNpcPlacements.TryDequeue(out message);

        /// <summary>Reports a journal entry this player earned. The host adds it and passes it on.</summary>
        /// <param name="path">The entry's resource path.</param>
        public static void ReportJournalEntry(string path)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(path)) return;

            NetworkAccess.SendToAll(new JournalEntryMessage { Paths = [path] });
        }

        /// <summary>Takes the next completion mark that arrived.</summary>
        public static bool TryTakeJournalTopic(out JournalTopicMessage message)
            => IncomingJournalTopics.TryDequeue(out message);

        /// <summary>Reports a quest's journal topic changing category here. The host applies it and passes it on.</summary>
        public static void ReportJournalTopic(in JournalTopicMessage message)
            => SendIfLive(message);

        /// <summary>Asks the host where its world has these actors. Client only.</summary>
        public static void AskNpcPlacement(in NpcPlacementRequestMessage message)
        {
            if (State != CoopSessionState.Joined) return;

            NetworkAccess.SendToAll(message);
        }

        /// <summary>Tells every client the host's weather. Host only, since everybody follows the host's dice.</summary>
        public static void BroadcastWeather(in WeatherMessage message)
            => SendIfHost(message);

        /// <summary>Takes the next clock report from the host.</summary>
        public static bool TryTakeDaytime(out DaytimeMessage message)
            => IncomingDaytime.TryDequeue(out message);

        /// <summary>Tells every client what time it is. Host only.</summary>
        public static void BroadcastDaytime(in DaytimeMessage message)
            => SendIfHost(message);

        /// <summary>Takes the next shared map unlock that arrived.</summary>
        public static bool TryTakeMapUnlock(out MapUnlockMessage message)
            => IncomingMapUnlocks.TryDequeue(out message);

        /// <summary>Reports a map unlocked here. The host applies it and passes it on.</summary>
        /// <param name="guid">The map definition's guid.</param>
        public static void ReportMapUnlock(string guid)
        {
            if (!IsLive) return;
            if (string.IsNullOrEmpty(guid)) return;

            NetworkAccess.SendToAll(new MapUnlockMessage { Guid = guid });
        }

        /// <summary>Takes the next dialogue reward that arrived.</summary>
        public static bool TryTakeDialogueExp(out DialogueExpMessage message)
            => IncomingDialogueExp.TryDequeue(out message);

        /// <summary>Reports a dialogue paying this player. The host pays and passes it on, sender excluded.</summary>
        public static void ReportDialogueExp(in DialogueExpMessage message)
            => SendIfLive(message);

        /// <summary>Takes the next dialogue item grant that arrived.</summary>
        public static bool TryTakeDialogueItems(out DialogueItemsMessage message)
            => IncomingDialogueItems.TryDequeue(out message);

        /// <summary>Reports a dialogue handing this player items. The host grants and passes it on, sender excluded.</summary>
        public static void ReportDialogueItems(in DialogueItemsMessage message)
            => SendIfLive(message);

        /// <summary>Reports a shared cutscene starting here. The host replays it and passes it on.</summary>
        public static void ReportCutscene(in CutsceneMessage message)
            => SendIfLive(message);

        /// <summary>Reports a cutscene here porting an NPC. The host passes it on.</summary>
        public static void ReportActorPort(in ActorPortMessage message)
            => SendIfLive(message);

        /// <summary>Reports a global variable this machine changed. The host applies it and passes it on.</summary>
        public static void ReportGVarChange(in GVarChangeMessage message)
            => SendIfLive(message);

        /// <summary>
        /// Tells every client these creatures are alive again. The push half of a revive, the answer half is
        /// <see cref="ForgetEnemyDeath"/>.
        /// </summary>
        public static void AnnounceEnemyRevive(List<string> guids)
        {
            if (State != CoopSessionState.Hosting || guids == null || guids.Count == 0) return;

            foreach (string[] chunk in guids.Chunk(EnemyReviveMessage.MaxGuids))
            {
                NetworkAccess.SendToAll(new EnemyReviveMessage { Guids = chunk });
            }
        }

        /// <summary>Takes the next authoritative health figure that has arrived.</summary>
        public static bool TryTakeEnemyHealth(out EnemyHealthMessage message)
            => IncomingEnemyHealth.TryDequeue(out message);

        /// <summary>Takes the next blow a client reported. Host only.</summary>
        public static bool TryTakeEnemyDamage(out EnemyDamageMessage message)
            => IncomingEnemyDamage.TryDequeue(out message);

        /// <summary>
        /// Asks the host to turn the world over because this player slept. Client only. The host decides, and the
        /// answer is the ordinary clock report.
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
        public static bool TryTakeDayAdvance(out DayAdvanceRequestMessage message)
            => IncomingDayAdvance.TryDequeue(out message);

        /// <summary>Takes the next engagement a client reported. Host only.</summary>
        public static bool TryTakeEnemyEngage(out EnemyEngageMessage message)
            => IncomingEnemyEngage.TryDequeue(out message);

        /// <summary>Takes the next engagement the host announced about itself. Clients only.</summary>
        public static bool TryTakeHostEngage(out EnemyEngageMessage message)
            => IncomingHostEngage.TryDequeue(out message);

        /// <summary>Takes the next teammate blow the host relayed into a local brawl. Clients only.</summary>
        public static bool TryTakeLeasedDamage(out EnemyDamageMessage message)
            => IncomingLeasedDamage.TryDequeue(out message);

        /// <summary>Tells every client what the host player is doing with one of its NPCs. Mirror of <see cref="ReportEnemyEngage"/>.</summary>
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
        /// Relays the host player's blow into a brawl a client runs. While leased, the client's opponents are the
        /// live ones and the host's are held.
        /// </summary>
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

            // The host is loading too. Answered now, the catch-up would be read from the world being left.
            if (_hostLoading)
            {
                if (!ResyncAfterLoad.Contains(peer)) ResyncAfterLoad.Add(peer);
                MelonLogger.Msg($"[GVar] player {peer.Id} asked for the world's variables while this world is loading; answering once it has.");
                return;
            }

            MelonLogger.Msg($"[GVar] player {peer.Id} asked for the world's variables again (" +
                (message.Reason == GVarResyncRequestMessage.ReasonSavegameLoaded
                    ? "they loaded a savegame"
                    : "asked for by hand") + "); sending the catch-up.");

            World.GVarSnapshot.SendTo(peer);
            World.JournalShare.SendTo(peer);

            // A loaded save lost its map unlocks too.
            World.MapShare.SendTo(peer);
        }

        private static bool _hostLoading;
        private static readonly List<INetPeer> ResyncAfterLoad = new();

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
                ResyncAfterLoad.Clear();
                return;
            }

            foreach (INetPeer peer in ResyncAfterLoad)
            {
                World.GVarSnapshot.SendTo(peer);
                World.JournalShare.SendTo(peer);
                World.MapShare.SendTo(peer);
            }

            if (ResyncAfterLoad.Count > 0) MelonLogger.Msg($"[GVar] this world's load landed; sent the catch-up to {ResyncAfterLoad.Count} waiting player(s).");
            ResyncAfterLoad.Clear();
        }

        /// <summary>
        /// Asks the host to send this world's variables again, since loading a save mid-session rewinds this
        /// machine's half of the world.
        /// </summary>
        /// <param name="reason">Why, for the host's log line.</param>
        /// <param name="replayUnchanged">Also run the consequences of values already held, to repair saves that stored values without running them.</param>
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

        /// <summary>Reports that this player worked a resource spot.</summary>
        public static void ReportResourceWork(in ResourceWorkMessage message)
            => SendIfLive(message);

        /// <summary>Takes the next reported swing, if one is waiting.</summary>
        public static bool TryTakeResourceWork(out ResourceWorkMessage message)
            => IncomingResourceWork.TryDequeue(out message);

        /// <summary>Takes the next chunk of the host's resource-spot table, if one is waiting.</summary>
        public static bool TryTakeResourceSpotSnapshot(out ResourceSpotSnapshotMessage message)
            => IncomingResourceSnapshot.TryDequeue(out message);

        /// <summary>Reports that one rubble pile has left this player's world.</summary>
        public static void ReportHaulPile(in HaulPileMessage message)
            => SendIfLive(message);

        /// <summary>Takes the next reported pile, if one is waiting.</summary>
        public static bool TryTakeHaulPile(out HaulPileMessage message)
            => IncomingHaulPiles.TryDequeue(out message);

        /// <summary>Reports a shared pickup or container looted here. See <see cref="World.LootShare"/>.</summary>
        public static void ReportLootTaken(in LootTakenMessage message)
        {
            foreach (LootTakenMessage part in message.Split()) SendIfLive(part);
        }

        /// <summary>Takes the next chunk of the loot snapshot a joining client was sent.</summary>
        public static bool TryTakeLootSnapshot(out LootSnapshotMessage message)
            => IncomingLootSnapshot.TryDequeue(out message);

        private static void OnLootTaken(INetPeer peer, LootTakenMessage message)
        {
            if (!message.IsPlausible()) return;
            if (!Gather(peer, ref message)) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (State != CoopSessionState.Hosting) return;

                // Relayed only under the shared rule: a client sharing on its own has no claim on other chests.
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
                if (LootMode != CoopProtocol.LootModeShared) return;

                message.Player = sender.Value;

                if (message.Kind == LootTakenMessage.KindProposal)
                {
                    // First offer per chest stands. Only the loser is told which one does.
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

        /// <summary>Reports a world object change here, like a door, lever or crate. See <see cref="World.WorldObjects"/>.</summary>
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

                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Player = sender.Value;

                World.WorldObjects.Park(message);
                SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!IsFromHost()) return;

            World.WorldObjects.Park(message);
        }

        /// <summary>Tells the attacker what this player's guard did with their blow. Routed through the host between clients.</summary>
        public static void ReportBlockVerdict(in BlockVerdictMessage message)
        {
            if (State == CoopSessionState.Joined)
            {
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

                // Attributed by connection, so a client cannot answer for a blow aimed at somebody else.
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
                if (message.Victim != sender) return;

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

        /// <summary>A thunder strike in the host's storm: to everyone.</summary>
        public static void BroadcastThunder()
        {
            if (State == CoopSessionState.Hosting) NetworkAccess.SendToAll(new ThunderMessage());
        }

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
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender) || message.To == sender) return;
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
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender) || message.To == sender) return;
                message.From = sender;

                if (message.To == LocalPlayerId) World.Trade.Take(message);
                else TrySendToPlayer(message, message.To);
                return;
            }

            if (IsFromHost() && message.To == LocalPlayerId) World.Trade.Take(message);
        }

        internal static int PeerCount => PeerToPlayer.Count;

        /// <summary>Reports what a breakable cache spilled, or what is left of it. See <see cref="World.CacheShare"/>.</summary>
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
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;
                if (LootMode != CoopProtocol.LootModeShared) return;

                message.Player = sender.Value;

                if (message.Kind == CacheSpillMessage.KindOffer)
                {
                    // First offer per cache stands. Only the loser is told which one does.
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
            // The arena is the host's to mark.
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

        /// <summary>Tells the session what this player's bars are out of. See <see cref="PlayerVitalsMessage"/>.</summary>
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

            VitalsByPlayer[LocalPlayerId] = message;
            NetworkAccess.SendToAll(message);
        }

        private static void OnPlayerVitals(INetPeer peer, PlayerVitalsMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.PlayerId = sender;
                VitalsByPlayer[sender] = message;

                SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            VitalsByPlayer[message.PlayerId] = message;
        }

        private static void OnNpcPlacementRequest(INetPeer peer, NpcPlacementRequestMessage message)
        {
            if (State != CoopSessionState.Hosting) return;

            World.NpcPlacement.Answer(peer, message);
        }

        private static void OnWeather(INetPeer peer, WeatherMessage message)
        {
            if (!IsFromHost()) return;

            // Superseded by the next report, so drop the oldest.
            EnqueueLatest(IncomingWeather, message, 4);
        }

        private static void OnDaytime(INetPeer peer, DaytimeMessage message)
        {
            if (!IsFromHost()) return;

            EnqueueLatest(IncomingDaytime, message, 4);
        }

        private static void OnGVarSnapshot(INetPeer peer, GVarSnapshotMessage message)
        {
            if (!IsFromHost()) return;
            if (message.Records == null) return;

            // Catch-up records go to their own queue: they are history and never play a transition.
            foreach (GVarSnapshotMessage.Record record in message.Records)
            {
                Enqueue(IncomingGVarCatchup, new GVarChangeMessage
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

            // Superseded by the next figure, so drop the oldest.
            EnqueueLatest(IncomingEnemyHealth, message, MaxPendingDamage);
        }

        private static void OnEnemyDamage(INetPeer peer, EnemyDamageMessage message)
        {
            // The host relaying a teammate's blow into a brawl this player runs locally. Deduped by sequence.
            if (State == CoopSessionState.Joined)
            {
                if (!IsFromHost()) return;

                if (_lastLeasedDamageSequence >= message.Sequence) return;
                _lastLeasedDamageSequence = message.Sequence;

                EnqueueLatest(IncomingLeasedDamage, message, MaxPendingDamage);
                return;
            }

            if (State != CoopSessionState.Hosting) return;

            if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

            message.Attacker = sender;

            // Counting a resend twice would double a player's damage.
            if (LastEnemyDamageSequence.TryGetValue(sender, out uint last) && message.Sequence <= last) return;

            LastEnemyDamageSequence[sender] = message.Sequence;

            EnqueueLatest(IncomingEnemyDamage, message, MaxPendingDamage);
        }

        private static void OnDayAdvanceRequest(INetPeer peer, DayAdvanceRequestMessage message)
        {
            // Only the host turns a world over.
            if (State != CoopSessionState.Hosting) return;

            if (!message.IsPlausible()) return;

            if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

            message.Player = sender;

            // Keep the oldest: dropping one would skip a night, not delay it. The host's cooldown absorbs repeats.
            Enqueue(IncomingDayAdvance, message, 4);
        }

        private static void OnEnemyEngage(INetPeer peer, EnemyEngageMessage message)
        {
            // The host's own engagements, so a teammate can join the fight.
            if (State == CoopSessionState.Joined)
            {
                if (!IsFromHost()) return;

                EnqueueLatest(IncomingHostEngage, message, MaxPendingDamage);
                return;
            }

            if (State != CoopSessionState.Hosting) return;

            if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

            message.Player = sender;

            // A newer engagement statement supersedes an older one.
            EnqueueLatest(IncomingEnemyEngage, message, MaxPendingDamage);
        }

        /// <summary>Queues something nothing will repeat. Full, it refuses the newest.</summary>
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

        /// <summary>Queues something the next message supersedes. Full, it drops the oldest.</summary>
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

        /// <summary>Tells a player this machine put a status effect on their body. Reliable, sent once per application.</summary>
        public static void ReportStatusEffect(PlayerId target, string effectId,
            byte origin = DamageReportMessage.OriginPlayer)
        {
            if (!IsLive) return;

            // Friendly fire applies to player effects, never to a creature's.
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
            byte next = NextStatusSequence.TryGetValue(target, out byte last) ? unchecked((byte)(last + 1)) : (byte)1;
            NextStatusSequence[target] = next;

            return next;
        }

        /// <summary>Takes the next status effect that has arrived for the local player, oldest first.</summary>
        public static bool TryTakeStatusEffect(out StatusEffectMessage message)
            => IncomingEffects.TryDequeue(out message);

        private static void OnStatusEffect(INetPeer peer, StatusEffectMessage message)
        {
            if (!IsLive) return;
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Source = sender;

                // The pair actually involved, as for a blow.
                if (message.Origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(sender, message.Target)) return;

                if (message.Target == sender) return;

                if (message.Target == LocalPlayerId)
                {
                    EnqueueEffect(message);
                    return;
                }

                if (!PlayerNames.ContainsKey(message.Target)) return;

                TrySendToPlayer(message, message.Target);
                return;
            }

            if (!IsFromHost()) return;
            if (message.Target != LocalPlayerId) return;
            if (message.Origin != DamageReportMessage.OriginCreature && !World.Arena.AllowsHarm(message.Source)) return;

            EnqueueEffect(message);
        }

        private static void EnqueueEffect(in StatusEffectMessage message)
        {
            // A replayed packet must be refused, since a second application is a second stack.
            if (LastStatusSequence.TryGetValue(message.Source, out byte last)
                && !PlayerStateMessage.IsNewerAction(message.Sequence, last))
            {
                return;
            }

            LastStatusSequence[message.Source] = message.Sequence;

            EnqueueLatest(IncomingEffects, message, MaxPendingDamage);
        }

        // Stops the transport and clears session state. Callers decide the resulting state.
        private static void Teardown(bool announce)
        {
            // A load that never landed must not hold the next session's catch-ups.
            _hostLoading = false;
            ResyncAfterLoad.Clear();
            HostSettings.Clear();

            if (NetworkAccess.Role != NetRole.None)
            {
                NetworkAccess.Stop();

                if (announce)
                {
                    MelonLogger.Msg("[Coop] Left the session.");
                }
            }

            PlayerNames.Clear();
            StateByPlayer.Clear();
            VitalsByPlayer.Clear();
            EquipmentByPlayer.Clear();
            TalentsByPlayer.Clear();
            EquipmentParts.Clear();
            TalentParts.Clear();
            LootParts.Clear();
            ItemUsesByPlayer.Clear();
            PeerToPlayer.Clear();
            RoundTripByPlayer.Clear();
            PingSentAt.Clear();
            IncomingDamage.Clear();
            IncomingEffects.Clear();
            IncomingKnowledge.Clear();
            IncomingExperience.Clear();
            IncomingSummonBinds.Clear();
            IncomingEnemyDeaths.Clear();

            // Per session only: across sessions the save remembers, and carrying it would suppress needed reports.
            DeadEnemies.Clear();
            IncomingCreatureOrders.Clear();
            IncomingLeaseSnapshots.Clear();
            IncomingGraveyard.Clear();
            IncomingAlive.Clear();
            IncomingRevive.Clear();
            IncomingGVars.Clear();
            IncomingGVarCatchup.Clear();
            IncomingResourceWork.Clear();
            IncomingLootSnapshot.Clear();
            IncomingResourceSnapshot.Clear();
            IncomingHaulPiles.Clear();
            IncomingCutscenes.Clear();
            IncomingActorPorts.Clear();
            IncomingWeather.Clear();
            IncomingJournal.Clear();
            IncomingJournalTopics.Clear();
            IncomingNpcPlacements.Clear();
            IncomingDaytime.Clear();
            IncomingMapUnlocks.Clear();
            IncomingDialogueExp.Clear();
            IncomingDialogueItems.Clear();
            IncomingEnemyHealth.Clear();
            IncomingEnemyDamage.Clear();
            IncomingEnemyEngage.Clear();
            IncomingHostEngage.Clear();
            IncomingLeasedDamage.Clear();
            IncomingDayAdvance.Clear();
            _lastLeasedDamageSequence = 0;
            IncomingSnapshots.Clear();

            // A spawn built after the session ends would stand in a world its announcement no longer describes.
            IncomingSpawns.Clear();
            LastEnemyDamageSequence.Clear();
            _nextEnemyDamageSequence = 0;
            LastStatusSequence.Clear();
            LastDamageSequence.Clear();
            _nextDamageSequence = 0;
            NextStatusSequence.Clear();
            Clock.Reset();
            LocalPlayerId = PlayerId.None;
            _connectingSeconds = 0f;
            ImplausibleStates = 0;
            _sincePing = 0f;

            // Back to the single-player answers, since outside a session there is no host.
            AdoptRules(false, CoopProtocol.DefaultRecoveryHealthPercent, CoopProtocol.DefaultEnemyHealthPercent, CoopProtocol.LootModeInstanced);
        }

        // Teardown first, because the failure reason must outlive it.
        /// <summary>Ends the session with a reason the player is shown.</summary>
        internal static void FailWith(string reason) => Fail(reason);

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

            // Every way of starting a session passes here, so a withdrawn build stays withdrawn.
            string? refusal = VersionCheck.Refusal;
            if (refusal != null)
            {
                Fail(refusal);
                return false;
            }

            State = CoopSessionState.Connecting;

            // Told before the handshake, so the first save while connecting is backed up as pre-session state.
            SaveBackups.NoteSessionBegan();

            return true;
        }

        private static void OnHostStarted()
        {
            State = CoopSessionState.Hosting;

            // From now on the host's settings are the session's rules. TickRules keeps them in step.
            AdoptRules(CoopPreferences.FriendlyFire, CoopPreferences.RecoveryHealthPercent, CoopPreferences.EnemyHealthPercent, CoopPreferences.LootMode);

            LocalPlayerId = PlayerId.Host;
            PlayerNames[PlayerId.Host] = Sanitize(PlayerIdentity.LocalName(), PlayerId.Host);

            string where = SessionCode != null ? $"session code {SessionCode}" : "a direct session";
            string sealing = IsEncrypted ? "encrypted" : "not encrypted, anyone relaying can read it";
            MelonLogger.Msg($"[Coop] Hosting {where}, {sealing}.");
        }

        private static void OnConnectedToHost()
        {
            // Not "joined" yet: the host still has to accept the version.
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

            // A host losing a client keeps going, and must tell everyone else since only it sees the event.
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (PeerToPlayer.Remove(peer.Id, out PlayerId departed))
                {
                    ForgetPlayer(departed);
                    World.Checkpoint.PeerLeft(peer.Id);

                    PlayerLeftMessage left = new() { Id = departed };
                    NetworkAccess.SendToAll(left);
                }

                return;
            }

            // A client that lost the host has no session left.
            if (State != CoopSessionState.Connecting && State != CoopSessionState.Joined) return;

            if (reason == DisconnectReason.Kicked)
            {
                Fail("The host removed you from the session.");
                return;
            }

            // Leave, not just Idle, because the transport is still up in Client role.
            Leave();
        }

        /// <summary>
        /// Host only: keeps the rules in step with its settings and tells everyone when they move. Polled because
        /// the game's config has no change event.
        /// </summary>
        private static void TickRules()
        {
            if (NetworkAccess.Role != NetRole.Host) return;

            bool friendlyFire = CoopPreferences.FriendlyFire;
            byte recovery = CoopPreferences.RecoveryHealthPercent;
            ushort enemyHealth = CoopPreferences.EnemyHealthPercent;
            byte lootMode = CoopPreferences.LootMode;
            byte gameMode = HostSettings.LocalGameMode();

            if (friendlyFire == FriendlyFire && recovery == RecoveryHealthPercent && enemyHealth == EnemyHealthPercent && lootMode == LootMode
                && gameMode == HostSettings.HostGameMode) return;

            AdoptRules(friendlyFire, recovery, enemyHealth, lootMode);
            HostSettings.HostGameMode = gameMode;

            // The whole set, so any one message is correct regardless of loss or order.
            NetworkAccess.SendToAll(new SessionRulesMessage
            {
                FriendlyFire = FriendlyFire,
                RecoveryHealthPercent = RecoveryHealthPercent,
                EnemyHealthPercent = EnemyHealthPercent,
                LootMode = LootMode,
                GameMode = HostSettings.HostGameMode,
            });
        }

        private static void OnSessionRules(INetPeer peer, SessionRulesMessage message)
        {
            // Only the host sets rules.
            if (NetworkAccess.Role != NetRole.Client) return;
            if (State != CoopSessionState.Joined) return;

            AdoptRules(message.FriendlyFire, message.RecoveryHealthPercent, message.EnemyHealthPercent, message.LootMode);
            HostSettings.HostGameMode = message.GameMode;
        }

        // Records the rules and logs each actual change. Zero means "not stated" and falls back to the
        // protocol default, and enemy health is clamped so a peer cannot make every blow land nothing.
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
            // Every message type is registered on both sides, so who may say what is checked in the handler.
            if (NetworkAccess.Role != NetRole.Host) return;

            // One handshake per peer, or a repeat would hand out a second id.
            if (PeerToPlayer.ContainsKey(peer.Id)) return;

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
            PeerToPlayer[peer.Id] = assigned;
            PlayerNames[assigned] = name;

            // The host stops being alone, which is what SinglePlayerOnly work is gated on.
            CoopProvider.NotifyChanged();

            // A zero hash means the other side computed none.
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

                // The latched values, not a fresh read, so a late joiner gets the same rules as everyone else.
                Rules = new SessionRulesMessage
                {
                    FriendlyFire = FriendlyFire,
                    RecoveryHealthPercent = RecoveryHealthPercent,
                    EnemyHealthPercent = EnemyHealthPercent,
                    LootMode = LootMode,
                    GameMode = HostSettings.LocalGameMode(),
                },
            };

            NetworkAccess.Send(peer, result);

            SendRoster(peer, exclude: assigned);
            HostSettings.SendAll(peer);

            PlayerJoinedMessage joined = new() { Id = assigned, Name = name };
            SendToClientsExcept(joined, peer.Id);

            // Nothing mentions an earlier death again, and the game reads a missing save record as alive.
            // The save's own corpses ride along for deaths from before any session.
            HashSet<string> graveyard = new(DeadEnemies, StringComparer.Ordinal);
            World.EnemyRegistry.CollectSaveDead(graveyard);
            SendGraveyard(peer, graveyard);

            // Quest state set before the join is never mentioned again on its own.
            World.GVarSnapshot.SendTo(peer);

            if (World.WeatherSync.TryBuildCurrent(out WeatherMessage weather))
            {
                NetworkAccess.Send(peer, weather);
            }

            // The quest graphs that write the journal never cross, so it must be sent whole.
            World.JournalShare.SendTo(peer);

            if (World.DaytimeSync.TryBuildCurrent(out DaytimeMessage daytime))
            {
                NetworkAccess.Send(peer, daytime);
            }

            // A dialogue handed the maps to one player only.
            World.MapShare.SendTo(peer);
            World.ResourceWork.SendTo(peer);
            World.LootShare.SendTo(peer);
            World.WorldObjects.SendTo(peer);
            World.Arena.SendTo(peer);
            World.CacheShare.SendTo(peer);

            MelonLogger.Msg($"[Coop] {name} joined as {assigned} ({PlayerNames.Count} in session).");
        }

        private static void OnHandshakeResult(INetPeer peer, HandshakeResultMessage message)
        {
            // Only a client is owed a result. Otherwise any peer could send a refusal and tear down the host's session.
            if (NetworkAccess.Role != NetRole.Client) return;

            // Only while a join is in flight, so a second result cannot unseat a live session.
            if (State != CoopSessionState.Connecting) return;

            if (!message.Accepted)
            {
                Fail(Describe(message));
                return;
            }

            State = CoopSessionState.Joined;
            _connectingSeconds = 0f;

            LocalPlayerId = message.AssignedPlayerId;
            PlayerNames[LocalPlayerId] = Sanitize(PlayerIdentity.LocalName(), LocalPlayerId);

            // The host's rules, never the local settings.
            AdoptRules(message.Rules.FriendlyFire, message.Rules.RecoveryHealthPercent, message.Rules.EnemyHealthPercent, message.Rules.LootMode);
            HostSettings.HostGameMode = message.Rules.GameMode;

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

                PlayerNames[entry.Id] = Sanitize(entry.Name, entry.Id);
            }

            MelonLogger.Msg($"[Coop] Roster received: {PlayerNames.Count} in session.");
        }

        private static void OnPlayerJoined(INetPeer peer, PlayerJoinedMessage message)
        {
            if (!IsFromHost()) return;
            if (message.Id == LocalPlayerId) return;

            PlayerNames[message.Id] = Sanitize(message.Name, message.Id);

            CoopProvider.NotifyChanged();

            MelonLogger.Msg($"[Coop] {PlayerNames[message.Id]} joined as {message.Id} ({PlayerNames.Count} in session).");
        }

        private static void OnPlayerLeft(INetPeer peer, PlayerLeftMessage message)
        {
            if (!IsFromHost()) return;

            ForgetPlayer(message.Id);
        }

        private static void ForgetPlayer(PlayerId player)
        {
            PlayerNames.Remove(player);

            // Ids are reused: the next joiner must not inherit a departed player's arena team.
            if (State == CoopSessionState.Hosting) World.Arena.SetTeam(player, 0);

            // The host may be alone again, and session-stopped mods have no other way to hear it.
            CoopProvider.NotifyChanged();

            StateByPlayer.Remove(player);
            VitalsByPlayer.Remove(player);
            EquipmentByPlayer.Remove(player);
            TalentsByPlayer.Remove(player);
            EquipmentParts.Remove(player);
            TalentParts.Remove(player);
            ItemUsesByPlayer.Remove(player);

            // Ids are reused and the next holder counts from one, so a stale high-water mark would swallow their hits.
            LastDamageSequence.Remove(player);
            LastEnemyDamageSequence.Remove(player);
            LastStatusSequence.Remove(player);
            NextStatusSequence.Remove(player);
            RoundTripByPlayer.Remove(player);
            World.EnemyEngagements.ForgetPlayer(player);
            World.RemoteGVar.ForgetOrigin(player.Value);

            // Rubble piles they carried off and never dumped go back so this world can haul them.
            World.HaulShare.RepayOrphans(player.Value);

            MelonLogger.Msg($"[Coop] {player} left ({PlayerNames.Count} in session).");
        }

        private static void OnPlayerState(INetPeer peer, PlayerStateMessage message)
        {
            // A NaN lands in the transform and poisons every later interpolation, and a host must not relay it.
            // Counted, not logged, so a peer cannot fill the disk.
            if (!message.IsPlausible())
            {
                ImplausibleStates++;
                return;
            }

            if (NetworkAccess.Role == NetRole.Host)
            {
                // The sender does not get to say who it is.
                if (!PeerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.PlayerId = sender;
                StateByPlayer[sender] = message;

                // One broadcast instead of a copy per client: receivers skip their own state, and the relay fans it out.
                NetworkAccess.SendToAll(message, Delivery.Sequenced);
                return;
            }

            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            StateByPlayer[message.PlayerId] = message;
        }

        // In a relay session a client can receive packets from other clients too.
        private static bool IsFromHost()
        {
            return NetworkAccess.Role == NetRole.Client && State == CoopSessionState.Joined;
        }

        // Lowest free id, so players coming and going never exhaust the ids.
        private static bool TryAssignPlayerId(out PlayerId assigned)
        {
            for (byte candidate = 1; candidate <= CoopProtocol.MaxClients; candidate++)
            {
                PlayerId id = new(candidate);
                if (PlayerNames.ContainsKey(id)) continue;

                assigned = id;
                return true;
            }

            assigned = PlayerId.None;
            return false;
        }

        private static void SendRoster(INetPeer peer, PlayerId exclude)
        {
            RosterEntry[] entries = PlayerNames.Where(p => p.Key != exclude).Select(p => new RosterEntry(p.Key, p.Value)).ToArray();

            foreach (RosterEntry[] chunk in entries.Chunk(RosterSnapshotMessage.MaxPlayers))
            {
                NetworkAccess.Send(peer, new RosterSnapshotMessage { Players = chunk });
            }
        }

        // Host only: the peer-to-player table is the host's alone.
        private static bool TrySendToPlayer<T>(T message, PlayerId player, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            int peerId = -1;
            foreach (KeyValuePair<int, PlayerId> mapping in PeerToPlayer)
            {
                if (mapping.Value != player) continue;

                peerId = mapping.Key;
                break;
            }

            if (peerId < 0) return false;

            PeerBuffer.Clear();
            NetworkAccess.GetPeers(PeerBuffer);

            foreach (INetPeer candidate in PeerBuffer)
            {
                if (candidate.Id != peerId) continue;

                NetworkAccess.Send(candidate, message, delivery);
                return true;
            }

            return false;
        }

        // The API's relay broadcast has no exclusion list, so the host unicasts.
        private static void SendToClientsExcept<T>(T message, int exceptPeerId, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            PeerBuffer.Clear();
            NetworkAccess.GetPeers(PeerBuffer);

            foreach (INetPeer other in PeerBuffer)
            {
                if (other.Id == exceptPeerId) continue;

                NetworkAccess.Send(other, message, delivery);
            }
        }

        // A name is free text that lands in the log, and a newline would let the sender forge log lines.
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
