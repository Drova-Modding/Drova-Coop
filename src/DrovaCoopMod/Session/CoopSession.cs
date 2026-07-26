using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
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

        private static readonly Dictionary<PlayerId, string> _players = new();
        private static readonly Dictionary<PlayerId, PlayerStateMessage> _latestStates = new();

        // Host only. A transport peer id means nothing on another machine, so the host is the only thing
        // that can translate between the two, and it is the only thing that ever needs to.
        private static readonly Dictionary<int, PlayerId> _peerToPlayer = new();

        // Reused so relaying state does not allocate a list per player per frame.
        private static readonly List<INetPeer> _peerBuffer = new();

        private static bool _initialized;
        private static float _connectingSeconds;

        /// <summary>
        /// Where the local player is in getting into a session.
        /// </summary>
        public static CoopSessionState State { get; private set; } = CoopSessionState.Idle;

        /// <summary>
        /// Who the local player is in this session. <see cref="PlayerId.Host"/> when hosting, whatever the
        /// host assigned when joined, and <see cref="PlayerId.None"/> outside a session.
        /// </summary>
        public static PlayerId LocalPlayerId { get; private set; } = PlayerId.None;

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
            NetworkAccess.RegisterWithId<RosterSnapshotMessage>(MessageIds.RosterSnapshot, OnRosterSnapshot);
            NetworkAccess.RegisterWithId<PlayerJoinedMessage>(MessageIds.PlayerJoined, OnPlayerJoined);
            NetworkAccess.RegisterWithId<PlayerLeftMessage>(MessageIds.PlayerLeft, OnPlayerLeft);
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
            NetworkAccess.Unregister<RosterSnapshotMessage>();
            NetworkAccess.Unregister<PlayerJoinedMessage>();
            NetworkAccess.Unregister<PlayerLeftMessage>();
            NetworkAccess.Unregister<PlayerStateMessage>();

            _initialized = false;
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
                PlayerId = LocalPlayerId,
                X = x,
                Y = y,
                Facing = facing,
            };

            NetworkAccess.SendToAll(message, Delivery.Unreliable);
        }

        // ------------------------------------------------------------------ session lifecycle

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
            _peerToPlayer.Clear();
            LocalPlayerId = PlayerId.None;
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

            // The host is player 0 by definition and needs no handshake to learn it.
            LocalPlayerId = PlayerId.Host;
            _players[PlayerId.Host] = Sanitize(CoopPreferences.PlayerName, PlayerId.Host);

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
            MelonLogger.Msg($"[Coop] Peer {peer.Id} left: {reason}.");

            // A host losing a client keeps going; that is most of the point of being the host. It does have
            // to tell everyone else, because nothing else can: only the host sees transport events for a
            // peer that is not itself.
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (_peerToPlayer.Remove(peer.Id, out PlayerId departed))
                {
                    _players.Remove(departed);
                    _latestStates.Remove(departed);

                    PlayerLeftMessage left = new() { Id = departed };
                    NetworkAccess.SendToAll(left);
                    MelonLogger.Msg($"[Coop] {departed} left ({_players.Count} in session).");
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

        private static void OnConnectionRejected(ConnectionRejection rejection)
        {
            Fail(Describe(rejection));
        }

        private static void OnNetworkError(string description)
        {
            MelonLogger.Error("[Coop] Transport error: " + description);
        }

        // ------------------------------------------------------------------ handshake and identity

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

            HandshakeResultMessage result = new()
            {
                Accepted = true,
                HostProtocolVersion = ProtocolVersion,
                AssignedPlayerId = assigned,
                Refusal = HandshakeRefusal.None,
            };

            NetworkAccess.Send(peer, result);

            // The newcomer gets everyone already in; everyone already in gets told about the newcomer.
            // Both are needed and neither covers the other.
            NetworkAccess.Send(peer, BuildRoster(exclude: assigned));

            PlayerJoinedMessage joined = new() { Id = assigned, Name = name };
            SendToClientsExcept(joined, peer.Id);

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
            _players[LocalPlayerId] = Sanitize(CoopPreferences.PlayerName, LocalPlayerId);

            MelonLogger.Msg($"[Coop] The host accepted the handshake; this client is {LocalPlayerId}.");
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
            MelonLogger.Msg($"[Coop] {_players[message.Id]} joined as {message.Id} ({_players.Count} in session).");
        }

        private static void OnPlayerLeft(INetPeer peer, PlayerLeftMessage message)
        {
            if (!IsFromHost()) return;

            _players.Remove(message.Id);
            _latestStates.Remove(message.Id);
            MelonLogger.Msg($"[Coop] {message.Id} left ({_players.Count} in session).");
        }

        private static void OnPlayerState(INetPeer peer, PlayerStateMessage message)
        {
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
                SendToClientsExcept(message, peer.Id, Delivery.Unreliable);
                return;
            }

            // A client trusts what the host stamped, because the host is the authority on identity. Its own
            // state coming back would be an echo it has no use for.
            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            _latestStates[message.PlayerId] = message;
        }

        // ------------------------------------------------------------------ helpers

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
                ConnectionRejection.BadSessionCode => "That session code is not valid for this relay.",
                ConnectionRejection.BadHandshake => "The relay speaks a different protocol version. Update the mod or the relay.",
                _ => "The connection was refused.",
            };
        }
    }
}
