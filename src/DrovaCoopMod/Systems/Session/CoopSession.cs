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

        /// <summary>
        /// How long a join may sit in <see cref="CoopSessionState.Connecting"/>. A host without the mod accepts
        /// the connection and never answers the handshake.
        /// </summary>
        private const float ConnectTimeoutSeconds = 15f;

        private const float PingIntervalSeconds = 2f;

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

        // Host only. A transport peer id means nothing on another machine.
        private static readonly Dictionary<int, PlayerId> PeerToPlayer = new();

        private static readonly List<INetPeer> PeerBuffer = new();

        private static readonly Dictionary<PlayerId, float> RoundTripByPlayer = new();
        private static readonly Dictionary<uint, double> PingSentAt = new();

        // A real clock: summed frame deltas quantize a round trip to whole frames.
        private static readonly System.Diagnostics.Stopwatch Clock = new();

        private static readonly List<Action> Unregisters = new();

        private static bool _initialized;
        private static float _connectingSeconds;
        private static float _sincePing;
        private static uint _nextPingToken;
        private static IReadOnlyList<ICoopSystem> _systems = [];
        private static CoopSessionState _state = CoopSessionState.Idle;

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
        /// on every machine. See <see cref="EnemyScaling"/>.
        /// </summary>
        public static ushort EnemyHealthPercent { get; private set; } = CoopProtocol.DefaultEnemyHealthPercent;

        /// <summary>The host's loot rule. See <see cref="LootShare"/>.</summary>
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
            NetworkEvents.OnConnectionRejected += OnConnectionRejected;
            NetworkEvents.OnNetworkError += OnNetworkError;

            _initialized = true;
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

        /// <summary>Advances the session's timers.</summary>
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

            NetworkAccess.SendToAll(message with { PlayerId = LocalPlayerId }, Delivery.Sequenced);
        }

        // Stops the transport and clears session state. Callers decide the resulting state.
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
            RoundTripByPlayer.Clear();
            PingSentAt.Clear();

            foreach (ICoopSystem system in _systems) system.EndSession();

            Clock.Reset();
            LocalPlayerId = PlayerId.None;
            _connectingSeconds = 0f;
            ImplausibleStates = 0;
            _sincePing = 0f;

            // Back to the single-player answers, since outside a session there is no host.
            AdoptRules(false, CoopProtocol.DefaultRecoveryHealthPercent, CoopProtocol.DefaultEnemyHealthPercent, CoopProtocol.LootModeInstanced);
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
                    Checkpoint.PeerLeft(peer.Id);

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
