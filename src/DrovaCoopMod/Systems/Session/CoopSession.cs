using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Loot;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// The session layer: transport events to UI state, the roster, the version handshake and player identity.
    /// Runs on the Unity main thread, where the transport delivers.
    /// </summary>
    public static partial class CoopSession
    {
        /// <summary>The coop protocol version, defined in the protocol assembly so the headless peer and tests share it.</summary>
        private const ushort ProtocolVersion = CoopProtocol.Version;

        /// <summary>A host without the mod accepts the connection and never answers the handshake.</summary>
        private const float ConnectTimeoutSeconds = 15f;

        private const float PingIntervalSeconds = 2f;

        private const float GameModeRefreshSeconds = 1f;

        /// <summary>A later reply would measure the timeout, not the link.</summary>
        private const float PingExpirySeconds = 10f;

        /// <summary>Unapplied blows held before the oldest is dropped, so a reload does not deliver a backlog at once.</summary>
        internal const int MaxPendingDamage = 32;

        /// <summary>Unapplied messages from the host held per queue before further ones are refused.</summary>
        internal const int MaxPendingFromHost = 512;

        /// <summary>Bounds what a peer can make this machine hold of a list sent in parts. Past it the parts are dropped.</summary>
        internal const int MaxGathered = 255;

        private static readonly Dictionary<PlayerId, string> PlayerNames = new();
        private static readonly Dictionary<PlayerId, PlayerStateMessage> StateByPlayer = new();

        /// <summary>Host only. A transport peer id means nothing on another machine.</summary>
        private static readonly Dictionary<int, PlayerId> PeerToPlayer = new();

        private static readonly List<INetPeer> PeerBuffer = new();

        private static readonly Dictionary<PlayerId, float> RoundTripByPlayer = new();
        private static readonly Dictionary<uint, double> PingSentAt = new();

        /// <summary>A real clock, since summed frame deltas quantize a round trip to whole frames.</summary>
        private static readonly System.Diagnostics.Stopwatch Clock = new();

        private static readonly List<Action> Unregisters = new();

        /// <summary>Host only: connections that have not passed the handshake yet, with when they connected.</summary>
        private static readonly Dictionary<int, (INetPeer Peer, long ConnectedMs)> Unintroduced = new();
        private static readonly List<int> IntroducedBuffer = new();

        /// <summary>Queues that warned about being full, so the warning comes once per filling.</summary>
        private static readonly HashSet<object> FullQueues = new(ReferenceEqualityComparer.Instance);

        private static bool _initialized;
        private static float _connectingSeconds;
        private static string? _lastTransportError;
        private static float _sincePing;
        private static float _sinceGameMode = GameModeRefreshSeconds;
        private static byte _localGameMode = SessionRulesMessage.UnknownGameMode;
        private static uint _nextPingToken;
        private static IReadOnlyList<ICoopSystem> _systems = [];
        private static CoopSessionState _state = CoopSessionState.Idle;

        /// <summary>Where the local player is in getting into a session. Every change raises <c>CoopAccess.OnSessionChanged</c>.</summary>
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

        internal static bool IsLive => State is CoopSessionState.Hosting or CoopSessionState.Joined;

        /// <summary>Each player keeps their own world and only fighting each other is shared. Fixed for the session.</summary>
        internal static bool ArenaMode { get; private set; }

        /// <summary>A live session whose players share one world.</summary>
        internal static bool SharesWorld => IsLive && !ArenaMode;

        /// <summary>Hosting a shared world.</summary>
        internal static bool HostsWorld => State == CoopSessionState.Hosting && !ArenaMode;

        /// <summary>Joined to a shared world.</summary>
        internal static bool JoinedWorld => State == CoopSessionState.Joined && !ArenaMode;

        /// <summary><see cref="PlayerId.Host"/> when hosting, the host-assigned id when joined, <see cref="PlayerId.None"/> outside a session.</summary>
        public static PlayerId LocalPlayerId { get; private set; } = PlayerId.None;

        /// <summary>
        /// Whether players may damage each other. Always the host's, so nobody is hittable by someone they cannot hit back.
        /// It can change mid-session.
        /// </summary>
        public static bool FriendlyFire { get; private set; }

        /// <summary>Whether standing over a downed teammate gets them up, besides the hold prompt. The host's setting.</summary>
        public static bool StandNearRevive { get; private set; }

        /// <summary>Whether only the host starts quest dialogues. The host's setting.</summary>
        public static bool HostLeadsStory { get; private set; }

        /// <summary>Health a downed player stands up with, as a percentage of maximum. The host's setting.</summary>
        public static byte RecoveryHealthPercent { get; private set; } = CoopProtocol.DefaultRecoveryHealthPercent;

        /// <summary>Creature toughness as a percentage of authored health. The host's setting, so <see cref="EnemyScaling"/> scales a blow the same everywhere.</summary>
        public static ushort EnemyHealthPercent { get; private set; } = CoopProtocol.DefaultEnemyHealthPercent;

        /// <summary>How hard creatures hit players, in percent. The host's rule.</summary>
        public static ushort EnemyDamagePercent { get; private set; } = CoopProtocol.DefaultEnemyDamagePercent;

        /// <summary>The host's loot rule. See <see cref="LootShare"/>.</summary>
        public static byte LootMode { get; private set; } = CoopProtocol.LootModeInstanced;

        /// <summary>Why the last attempt failed, for the player. Null unless <see cref="State"/> is <see cref="CoopSessionState.Failed"/>.</summary>
        public static string? LastError { get; private set; }

        /// <summary>State updates refused for NaN or absurd numbers. Nonzero means a peer is sending nonsense.</summary>
        public static int ImplausibleStates { get; private set; }

        /// <summary>The seed shared rolls derive from, drawn by the host. 0 outside a session.</summary>
        internal static ulong SessionSeed { get; private set; }

        /// <summary>The UDP port of a direct session, hosted or joined. Zero in a Steam session.</summary>
        public static int DirectPort { get; private set; }

        /// <summary>The address a direct client joined. Null when hosting and in a Steam session.</summary>
        public static string? DirectAddress { get; private set; }

        /// <summary>Whether this session runs over Steam.</summary>
        public static bool ViaSteam { get; private set; }

        /// <summary>Everyone in the session, including the local player, keyed by host-assigned id.</summary>
        public static IReadOnlyDictionary<PlayerId, string> Players => PlayerNames;

        /// <summary>The most recent state each player sent.</summary>
        public static IReadOnlyDictionary<PlayerId, PlayerStateMessage> LatestStates => StateByPlayer;

        internal static int PeerCount => PeerToPlayer.Count;

        /// <summary>The player's name, or <c>player N</c> before it is known.</summary>
        internal static string NameOf(PlayerId player)
        {
            return PlayerNames.TryGetValue(player, out string? name) && !string.IsNullOrEmpty(name) ? name : $"player {player.Value}";
        }

        /// <summary>Registers the message types and subscribes to the transport. Only the first call does anything.</summary>
        /// <param name="systems">Each registers its own messages, and is told when a player leaves or the session ends.</param>
        internal static void Initialize(IReadOnlyList<ICoopSystem> systems)
        {
            if (_initialized) return;

            _systems = systems;

            Register<HandshakeMessage>(MessageIds.Handshake, OnHandshake);
            Register<HandshakeResultMessage>(MessageIds.HandshakeResult, OnHandshakeResult);
            Register<RosterSnapshotMessage>(MessageIds.RosterSnapshot, OnRosterSnapshot);
            Register<PlayerJoinedMessage>(MessageIds.PlayerJoined, OnPlayerJoined);
            Register<PlayerLeftMessage>(MessageIds.PlayerLeft, OnPlayerLeft);
            Register<SessionRulesMessage>(MessageIds.SessionRules, OnSessionRules);
            Register<PlayerStateMessage>(MessageIds.PlayerState, OnPlayerState);
            Register<PingMessage>(MessageIds.Ping, OnPing);

            foreach (ICoopSystem system in systems) system.RegisterMessages();

            NetworkEvents.OnHostStarted += OnHostStarted;
            NetworkEvents.OnConnectedToHost += OnConnectedToHost;
            NetworkEvents.OnPeerConnected += OnPeerConnected;
            NetworkEvents.OnPeerDisconnected += OnPeerDisconnected;
            NetworkEvents.OnNetworkError += OnNetworkError;

            NetworkAccess.SetDiscoveryInfo(LanInfo);

            _initialized = true;
        }

        /// <summary>Hosts directly on a UDP port, found by LAN discovery or joined by address. Nothing is encrypted.</summary>
        public static void HostDirect(int port)
        {
            if (!Begin()) return;

            DirectPort = port;
            MelonLogger.Msg($"[Coop] Hosting directly on UDP port {port}...");
            NetworkAccess.UsePortMapping = CoopPreferences.PortMapping;
            NetworkAccess.StartHost(port);

            if (NetworkAccess.Role == NetRole.None)
            {
                Fail($"Could not listen on UDP port {port}. Another program, or another game, may be using it.");
            }
        }

        /// <summary>Joins a direct host by address.</summary>
        public static void JoinDirect(string address, int port)
        {
            if (!Begin()) return;

            DirectAddress = address;
            DirectPort = port;
            MelonLogger.Msg($"[Coop] Joining {address}:{port} directly...");
            NetworkAccess.Connect(address, port);
        }

        /// <summary>Hosts over Steam, and shows Steam friends "Join Game".</summary>
        public static void HostViaSteam()
        {
            if (!Begin()) return;

            ViaSteam = true;
            MelonLogger.Msg("[Coop] Hosting over Steam...");
            SteamAccess.AllowDirectConnections = CoopPreferences.SteamDirect;
            NetworkAccess.StartHostViaSteam();

            if (NetworkAccess.Role == NetRole.None)
            {
                Fail("Steam is not running, or this is not the Steam copy of the game. Host over the internet or the network instead.");
                return;
            }

            SteamAccess.SetJoinString(SteamJoin.Format(SteamAccess.LocalId));
            if (CoopPreferences.PublicGame) PublicGames.List(CoopPreferences.ArenaMode);
        }

        /// <summary>Joins a Steam friend's game by their Steam id.</summary>
        public static void JoinViaSteam(ulong hostSteamId)
        {
            if (!Begin()) return;

            ViaSteam = true;
            MelonLogger.Msg($"[Coop] Joining {hostSteamId} over Steam...");
            SteamAccess.AllowDirectConnections = CoopPreferences.SteamDirect;
            NetworkAccess.ConnectViaSteam(hostSteamId);

            if (NetworkAccess.Role == NetRole.None)
            {
                Fail("Steam is not running, or this is not the Steam copy of the game.");
            }
        }

        /// <summary>Leaves any session and returns to <see cref="CoopSessionState.Idle"/>, forgetting the last failure.</summary>
        public static void Leave()
        {
            Teardown(announce: true);

            LastError = null;
            State = CoopSessionState.Idle;
        }

        /// <summary>Advances the session's timers.</summary>
        /// <param name="unscaledDeltaTime">Unscaled, because the transport keeps running while a menu pauses the world.</param>
        public static void Tick(float unscaledDeltaTime)
        {
            if (IsLive)
            {
                // The transport dropped. Without this the host keeps "hosting" a dead connection.
                if (NetworkAccess.Role == NetRole.None)
                {
                    Fail("The connection was lost. Host or join again to continue.");
                    return;
                }

                if (State == CoopSessionState.Hosting) DropUnintroduced();

                if (PublicGames.RefusesHardcore())
                {
                    Fail("A Hardcore hero only plays with friends: the host of a public game could end the run for good.");
                    return;
                }

                TickRules(unscaledDeltaTime);
                TickPing(unscaledDeltaTime);
                if (PublicGames.Due(unscaledDeltaTime)) PublicGames.Describe(LanInfo());
                return;
            }

            if (State != CoopSessionState.Connecting) return;

            // The transport gave up on its own, as Steam does when the host cannot be reached.
            if (NetworkAccess.Role == NetRole.None)
            {
                Fail("Could not reach the host" + (_lastTransportError == null ? "." : ": " + _lastTransportError + "."));
                return;
            }

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

        /// <summary>Drops every transport subscription and message registration, or a hot reload would handle every message twice.</summary>
        public static void Shutdown()
        {
            Leave();

            if (!_initialized) return;

            NetworkEvents.OnHostStarted -= OnHostStarted;
            NetworkEvents.OnConnectedToHost -= OnConnectedToHost;
            NetworkEvents.OnPeerConnected -= OnPeerConnected;
            NetworkEvents.OnPeerDisconnected -= OnPeerDisconnected;
            NetworkEvents.OnNetworkError -= OnNetworkError;
            NetworkAccess.SetDiscoveryInfo(null);

            foreach (Action unregister in Unregisters)
            {
                unregister();
            }

            Unregisters.Clear();

            _initialized = false;
        }

        /// <summary>Sequenced, so an overtaken datagram cannot put a stale stance back on the far copy.</summary>
        /// <param name="message">The caller fills in the intent. This stamps the id.</param>
        public static void BroadcastPlayerState(PlayerStateMessage message)
        {
            if (!IsLive) return;

            NetworkAccess.SendToAll(message with { PlayerId = LocalPlayerId }, Delivery.Sequenced);
        }

        /// <summary>Stops the transport and clears session state. Callers decide the resulting state.</summary>
        private static void Teardown(bool announce)
        {
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
            PeerToPlayer.Clear();
            Unintroduced.Clear();
            RoundTripByPlayer.Clear();
            PingSentAt.Clear();

            foreach (ICoopSystem system in _systems) system.EndSession();

            Clock.Reset();
            LocalPlayerId = PlayerId.None;
            SessionSeed = 0;
            _connectingSeconds = 0f;
            _lastTransportError = null;
            ImplausibleStates = 0;
            _sincePing = 0f;
            _sinceGameMode = GameModeRefreshSeconds;
            DirectPort = 0;
            DirectAddress = null;

            if (ViaSteam) SteamAccess.SetJoinString(null);
            ViaSteam = false;
            PublicGames.Unlist();

            // Back to the single-player answers, since outside a session there is no host.
            ArenaMode = false;
            AdoptRules(false, CoopProtocol.DefaultRecoveryHealthPercent, CoopProtocol.DefaultEnemyHealthPercent, CoopProtocol.DefaultEnemyDamagePercent,
                CoopProtocol.LootModeInstanced, false, false);
        }

        /// <summary>Ends the session with a reason the player is shown.</summary>
        internal static void FailWith(string reason) => Fail(reason);

        private static void Fail(string reason)
        {
            // Teardown first, because the failure reason must outlive it.
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
            NetworkAccess.StopDiscovery();

            // Told before the handshake, so the first save while connecting is backed up as pre-session state.
            SaveBackups.NoteSessionBegan();

            return true;
        }

        private static void OnHostStarted()
        {
            State = CoopSessionState.Hosting;
            SessionSeed = (ulong)Random.Shared.NextInt64() ^ ((ulong)Random.Shared.Next() << 63);

            // From now on the host's settings are the session's rules.
            ArenaMode = CoopPreferences.ArenaMode;
            AdoptRules(CoopPreferences.FriendlyFire, CoopPreferences.RecoveryHealthPercent, CoopPreferences.EnemyHealthPercent,
                CoopPreferences.EnemyDamagePercent, CoopPreferences.LootMode, CoopPreferences.StandNearRevive, CoopPreferences.HostLeadsStory);

            LocalPlayerId = PlayerId.Host;
            PlayerNames[PlayerId.Host] = Sanitize(PlayerIdentity.LocalName(), PlayerId.Host);

            string where = ViaSteam ? "for Steam friends" : $"directly on UDP port {DirectPort}";
            // Steam encrypts every session itself.
            string sealing = ViaSteam ? "encrypted" : "not encrypted, anyone on the way can read it";
            MelonLogger.Msg($"[Coop] Hosting {where}, {sealing}{(ArenaMode ? ", in arena mode: each player keeps their own world" : "")}.");
        }

        private static void OnConnectedToHost()
        {
            // Not joined yet, since the host still has to accept the version.
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

            if (NetworkAccess.Role == NetRole.Host) Unintroduced[peer.Id] = (peer, Environment.TickCount64);
        }

        /// <summary>Drops connections that sent no handshake in time, which would otherwise hear every broadcast.</summary>
        private static void DropUnintroduced()
        {
            if (Unintroduced.Count == 0) return;

            long now = Environment.TickCount64;
            IntroducedBuffer.Clear();

            foreach (KeyValuePair<int, (INetPeer Peer, long ConnectedMs)> entry in Unintroduced)
            {
                if (PeerToPlayer.ContainsKey(entry.Key))
                {
                    IntroducedBuffer.Add(entry.Key);
                    continue;
                }

                if (now - entry.Value.ConnectedMs >= (long)(ConnectTimeoutSeconds * 1000f)) IntroducedBuffer.Add(entry.Key);
            }

            // After the loop, since a Steam disconnect reports back at once and removes from the table.
            foreach (int id in IntroducedBuffer)
            {
                if (!Unintroduced.Remove(id, out (INetPeer Peer, long ConnectedMs) entry) || PeerToPlayer.ContainsKey(id)) continue;

                MelonLogger.Warning($"[Coop] Peer {id} sent no handshake within {ConnectTimeoutSeconds:0} s and was dropped.");
                entry.Peer.Disconnect();
            }
        }

        private static void OnPeerDisconnected(INetPeer peer, DisconnectReason reason)
        {
            MelonLogger.Msg($"[Coop] Peer {peer.Id} left: {reason}.");
            Unintroduced.Remove(peer.Id);

            // A host losing a client keeps going, and must tell everyone else since only it sees the event.
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (PeerToPlayer.Remove(peer.Id, out PlayerId departed))
                {
                    ForgetPlayer(departed);
                    Checkpoint.PeerLeft(peer.Id);

                    PlayerLeftMessage left = new() { Id = departed };
                    NetworkAccess.SendToAll(left);
                }

                return;
            }

            if (State != CoopSessionState.Connecting && State != CoopSessionState.Joined) return;

            // A direct host refuses only a wrong connection key, and LiteNetLib does not say why.
            if (reason == DisconnectReason.Rejected)
            {
                Fail("The host refused the connection.");
                return;
            }

            if (reason == DisconnectReason.ConnectionFailed && State == CoopSessionState.Connecting && DirectAddress != null)
            {
                Fail($"No host answered at {DirectAddress}:{DirectPort}. Check the address and port, and that the host's firewall lets Drova through.");
                return;
            }

            // Leave, not just Idle, because the transport is still up in Client role.
            Leave();
        }

        /// <summary>Host only: sends the rules when its settings move. Polled, since the game's config has no change event.</summary>
        private static void TickRules(float unscaledDeltaTime)
        {
            if (NetworkAccess.Role != NetRole.Host) return;

            // The savegame mode only moves on a load or in the options, so it is read once a second.
            _sinceGameMode += unscaledDeltaTime;
            if (_sinceGameMode >= GameModeRefreshSeconds)
            {
                _sinceGameMode = 0f;
                _localGameMode = HostSettings.LocalGameMode();
            }

            bool friendlyFire = CoopPreferences.FriendlyFire;
            byte recovery = CoopPreferences.RecoveryHealthPercent;
            ushort enemyHealth = CoopPreferences.EnemyHealthPercent;
            ushort enemyDamage = CoopPreferences.EnemyDamagePercent;
            byte lootMode = CoopPreferences.LootMode;
            bool standNear = CoopPreferences.StandNearRevive;
            bool hostLeads = CoopPreferences.HostLeadsStory;
            byte gameMode = _localGameMode;

            if (friendlyFire == FriendlyFire && recovery == RecoveryHealthPercent && enemyHealth == EnemyHealthPercent
                && enemyDamage == EnemyDamagePercent && lootMode == LootMode && standNear == StandNearRevive
                && hostLeads == HostLeadsStory && gameMode == HostSettings.HostGameMode) return;

            AdoptRules(friendlyFire, recovery, enemyHealth, enemyDamage, lootMode, standNear, hostLeads);
            HostSettings.HostGameMode = gameMode;

            // The whole set, so any one message is correct regardless of loss or order.
            NetworkAccess.SendToAll(new SessionRulesMessage
            {
                FriendlyFire = FriendlyFire,
                RecoveryHealthPercent = RecoveryHealthPercent,
                EnemyHealthPercent = EnemyHealthPercent,
                EnemyDamagePercent = EnemyDamagePercent,
                LootMode = LootMode,
                GameMode = HostSettings.HostGameMode,
                ArenaMode = ArenaMode,
                StandNearRevive = StandNearRevive,
                HostLeadsStory = HostLeadsStory,
            });
        }

        private static void OnSessionRules(INetPeer peer, SessionRulesMessage message)
        {
            if (NetworkAccess.Role != NetRole.Client) return;
            if (State != CoopSessionState.Joined) return;

            AdoptRules(message.FriendlyFire, message.RecoveryHealthPercent, message.EnemyHealthPercent, message.EnemyDamagePercent,
                message.LootMode, message.StandNearRevive, message.HostLeadsStory);
            HostSettings.HostGameMode = message.GameMode;
        }

        /// <summary>Zero falls back to the protocol default. The clamps stop a peer from making every blow land nothing.</summary>
        private static void AdoptRules(bool friendlyFire, byte recoveryHealthPercent, ushort enemyHealthPercent, ushort enemyDamagePercent,
            byte lootMode, bool standNearRevive, bool hostLeadsStory)
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

            if (enemyDamagePercent == 0) enemyDamagePercent = CoopProtocol.DefaultEnemyDamagePercent;
            enemyDamagePercent = Math.Clamp(enemyDamagePercent, CoopProtocol.MinEnemyDamagePercent, CoopProtocol.MaxEnemyDamagePercent);

            if (friendlyFire != FriendlyFire)
            {
                FriendlyFire = friendlyFire;
                MelonLogger.Msg($"[Coop] Friendly fire is now {(friendlyFire ? "ON - players can hurt each other" : "off")}.");
            }

            if (standNearRevive != StandNearRevive)
            {
                StandNearRevive = standNearRevive;
                MelonLogger.Msg(standNearRevive
                    ? "[Coop] Standing over a downed teammate now gets them up too."
                    : "[Coop] A downed teammate now gets up only through the hold prompt.");
            }

            if (hostLeadsStory != HostLeadsStory)
            {
                HostLeadsStory = hostLeadsStory;
                MelonLogger.Msg(hostLeadsStory
                    ? "[Coop] Only the host starts quest dialogues now."
                    : "[Coop] Every player can start quest dialogues now.");
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

            if (enemyDamagePercent != EnemyDamagePercent)
            {
                EnemyDamagePercent = enemyDamagePercent;
                MelonLogger.Msg($"[Coop] Creatures now hit with {enemyDamagePercent}% of their normal damage.");
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

        /// <summary>The answer to LAN discovery. The API sends it only while this machine hosts directly.</summary>
        private static byte[] LanInfo()
        {
            string name = PlayerNames.TryGetValue(PlayerId.Host, out string? host) ? host : "Host";
            return new LanGameInfo(ProtocolVersion, (byte)Math.Max(1, PlayerNames.Count), CoopProtocol.MaxClients + 1, name).Encode();
        }

        private static void OnNetworkError(string description)
        {
            MelonLogger.Error("[Coop] Transport error: " + description);
            _lastTransportError = description;
        }
    }
}
