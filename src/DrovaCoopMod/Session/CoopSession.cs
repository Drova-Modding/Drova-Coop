using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using MelonLoader;

namespace DrovaCoopMod.Session
{
    /// <summary>
    /// The mod's session layer: it turns the API's transport events into a state a UI can render, keeps
    /// the roster of who is in, and settles the version handshake. Everything above this - world sync,
    /// menus, lobby - builds on it; everything below it belongs to the Drova Modding API.
    ///
    /// All of it runs on the Unity main thread, because that is where the transport delivers.
    /// </summary>
    public static class CoopSession
    {
        /// <summary>
        /// The coop protocol version. Bump it whenever a message layout changes; two mods that disagree
        /// here refuse each other instead of desyncing later.
        /// </summary>
        public const ushort ProtocolVersion = 1;

        /// <summary>
        /// How long a join may sit in <see cref="CoopSessionState.Connecting"/> before it is given up on.
        /// Covers both halves of getting in: the transport connecting, and the host answering the
        /// handshake. A host without this mod installed accepts the connection and then says nothing at
        /// all, so without a deadline the client waits forever with no way to tell what went wrong.
        /// </summary>
        public const float ConnectTimeoutSeconds = 15f;

        private static readonly Dictionary<int, string> _players = new();
        private static readonly Dictionary<int, PlayerStateMessage> _latestStates = new();
        private static bool _initialized;
        private static float _connectingSeconds;

        /// <summary>
        /// Where the local player is in getting into a session.
        /// </summary>
        public static CoopSessionState State { get; private set; } = CoopSessionState.Idle;

        /// <summary>
        /// Why the last attempt failed, in words meant for a player. Null unless
        /// <see cref="State"/> is <see cref="CoopSessionState.Failed"/>.
        /// </summary>
        public static string? LastError { get; private set; }

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
        /// Peers in the session by their transport id, with the name they announced. The host appears on a
        /// client as soon as the handshake is accepted.
        /// </summary>
        public static IReadOnlyDictionary<int, string> Players => _players;

        /// <summary>
        /// The most recent state each player sent. Nothing applies these to the world yet - that is where
        /// the actual coop work starts.
        /// </summary>
        public static IReadOnlyDictionary<int, PlayerStateMessage> LatestStates => _latestStates;

        /// <summary>
        /// Registers the message types and subscribes to the transport. Safe to call more than once; only
        /// the first call does anything, because registering an id twice is an error by design.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            if (!NetworkAccess.IsSupported)
            {
                MelonLogger.Warning("[Coop] The installed Drova Modding API has no coop support, so this mod stays idle. Install the coop-enabled build.");
                return;
            }

            NetworkAccess.RegisterWithId<HandshakeMessage>(MessageIds.Handshake, OnHandshake);
            NetworkAccess.RegisterWithId<HandshakeResultMessage>(MessageIds.HandshakeResult, OnHandshakeResult);
            NetworkAccess.RegisterWithId<PlayerStateMessage>(MessageIds.PlayerState, OnPlayerState);

            NetworkEvents.OnHostStarted += OnHostStarted;
            NetworkEvents.OnConnectedToHost += OnConnectedToHost;
            NetworkEvents.OnPeerConnected += OnPeerConnected;
            NetworkEvents.OnPeerDisconnected += OnPeerDisconnected;
            NetworkEvents.OnConnectionRejected += OnConnectionRejected;
            NetworkEvents.OnNetworkError += OnNetworkError;

            _initialized = true;
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
        /// Hosts directly on a UDP port. Needs the port reachable from the internet, which is what the
        /// relay exists to avoid.
        /// </summary>
        public static void HostDirect(int port)
        {
            if (!Begin()) return;

            MelonLogger.Msg($"[Coop] Hosting directly on {port}...");
            NetworkAccess.StartHost(port);
        }

        /// <summary>
        /// Joins a direct host.
        /// </summary>
        public static void JoinDirect(string address, int port)
        {
            if (!Begin()) return;

            MelonLogger.Msg($"[Coop] Joining {address}:{port}...");
            NetworkAccess.Connect(address, port);
        }

        /// <summary>
        /// Leaves whatever session is running and returns to <see cref="CoopSessionState.Idle"/>, clearing
        /// any earlier failure. To end a session *and* leave a reason on screen, use the failure paths
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
            if (State != CoopSessionState.Connecting) return;

            _connectingSeconds += unscaledDeltaTime;
            if (_connectingSeconds < ConnectTimeoutSeconds) return;

            Fail($"No answer from the host within {ConnectTimeoutSeconds:0} seconds. It may be running a different mod set, or none.");
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

            NetworkAccess.Unregister<HandshakeMessage>();
            NetworkAccess.Unregister<HandshakeResultMessage>();
            NetworkAccess.Unregister<PlayerStateMessage>();

            _initialized = false;
        }

        /// <summary>
        /// Stops the transport and clears everything that describes a session, without deciding what state
        /// the mod ends up in. Both leaving and failing need this much; only they know what comes after.
        /// </summary>
        private static void Teardown(bool announce)
        {
            if (NetworkAccess.IsSupported && NetworkAccess.Role != NetRole.None)
            {
                NetworkAccess.Stop();

                if (announce)
                {
                    MelonLogger.Msg("[Coop] Left the session.");
                }
            }

            _players.Clear();
            _latestStates.Clear();
            _connectingSeconds = 0f;
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

        /// <summary>
        /// Sends the local player's state to everyone. A placeholder for the real sync loop: it shows the
        /// shape (unreliable, every frame, superseded by the next one) without pretending to sync a world.
        /// </summary>
        public static void BroadcastPlayerState(float x, float y, float facing)
        {
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;

            PlayerStateMessage message = new()
            {
                PlayerId = 0,
                X = x,
                Y = y,
                Facing = facing,
            };

            NetworkAccess.SendToAll(message, Delivery.Unreliable);
        }

        private static bool Begin()
        {
            if (!NetworkAccess.IsSupported)
            {
                Fail("This Drova Modding API build has no coop support.");
                return false;
            }

            Leave();
            State = CoopSessionState.Connecting;
            return true;
        }

        private static void OnHostStarted()
        {
            State = CoopSessionState.Hosting;

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
                PlayerName = CoopPreferences.PlayerName,
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
            _players.Remove(peer.Id);
            _latestStates.Remove(peer.Id);
            MelonLogger.Msg($"[Coop] Peer {peer.Id} left: {reason}.");

            // A host losing a client keeps going; that is most of the point of being the host.
            if (NetworkAccess.Role == NetRole.Host) return;

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

            // One handshake per peer. Repeating it costs the sender nothing and would otherwise rewrite the
            // roster entry and print a line every time, which is a log flood with extra steps.
            if (_players.ContainsKey(peer.Id)) return;

            bool accepted = message.ProtocolVersion == ProtocolVersion;

            HandshakeResultMessage result = new()
            {
                Accepted = accepted,
                HostProtocolVersion = ProtocolVersion,
            };

            NetworkAccess.Send(peer, result);

            if (!accepted)
            {
                MelonLogger.Warning($"[Coop] Refused peer {peer.Id}: coop protocol {message.ProtocolVersion}, this host speaks {ProtocolVersion}.");
                peer.Disconnect();
                return;
            }

            string name = Sanitize(message.PlayerName, peer.Id);
            _players[peer.Id] = name;
            MelonLogger.Msg($"[Coop] {name} joined ({_players.Count} in session).");
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
                Fail($"The host runs coop protocol {message.HostProtocolVersion}, this mod runs {ProtocolVersion}. Same version on both sides is required.");
                return;
            }

            State = CoopSessionState.Joined;
            _connectingSeconds = 0f;
            _players[peer.Id] = "Host";
            MelonLogger.Msg("[Coop] The host accepted the handshake.");
        }

        /// <summary>
        /// Makes a name from a stranger safe to print. The length cap is the message's job; this is about
        /// content, because a name is free text that lands in the log, and a newline in it lets the sender
        /// forge whole log lines.
        /// </summary>
        private static string Sanitize(string? name, int peerId)
        {
            if (string.IsNullOrWhiteSpace(name)) return $"Player {peerId}";

            char[] cleaned = name!.ToCharArray();
            for (int i = 0; i < cleaned.Length; i++)
            {
                if (char.IsControl(cleaned[i]))
                {
                    cleaned[i] = ' ';
                }
            }

            string result = new string(cleaned).Trim();
            return result.Length == 0 ? $"Player {peerId}" : result;
        }

        private static void OnPlayerState(INetPeer peer, PlayerStateMessage message)
        {
            // Stored, not applied. Turning these into moving characters is the next real piece of work and
            // deliberately not faked here.
            _latestStates[peer.Id] = message;
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
                ConnectionRejection.BadSessionCode => "That session code is not valid for this relay.",
                ConnectionRejection.BadHandshake => "The relay speaks a different protocol version. Update the mod or the relay.",
                _ => "The connection was refused.",
            };
        }
    }
}
