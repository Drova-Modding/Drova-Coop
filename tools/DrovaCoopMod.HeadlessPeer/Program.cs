using System.Reflection;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;

namespace DrovaCoopMod.HeadlessPeer
{
    /// <summary>
    /// Stands in for the second player. Joins or hosts a coop session over a relay using the real API
    /// transport, completes the coop handshake, and optionally streams player state so the other side has
    /// something moving to render.
    ///
    /// The point is to make the net layer testable with one running game instead of two, and testable
    /// with none when the far side is another headless peer.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// <c>Actor.EMoveMode.Run</c>. Hardcoded rather than referenced: the enum belongs to the game's
        /// assemblies, and this tool must keep building on a machine that has none.
        /// </summary>
        private const byte MoveModeRun = 2;

        /// <summary>
        /// <c>Actor.EMoveMode.Stand</c>. Sent while idling, so the far end can take its position
        /// correction at a moment when repositioning cannot be seen.
        /// </summary>
        private const byte MoveModeStand = 0;

        private static bool _handshakeAccepted;
        private static bool _handshakeAnswered;
        private static int _statesReceived;
        private static PlayerId _localPlayerId = PlayerId.None;
        private static float _centerX;
        private static float _centerY;
        private static bool _centerKnown;
        private static float _positionX;
        private static float _positionY;
        private static float _lastObservedX;
        private static float _lastObservedY;
        private static DateTime _observedAt;
        private static int _lastObservedTime;
        private static float _peakObservedSpeed;
        private static PlayerStateMessage _mirrorOf;
        private static bool _mirrorSeen;

        // Host side only, mirroring what CoopSession does: transport peer ids are local to this machine,
        // so the host is the only thing that can map them to ids everyone agrees on.
        private static readonly Dictionary<int, PlayerId> _peerToPlayer = new();
        private static readonly Dictionary<PlayerId, string> _players = new();

        /// <summary>
        /// Runs until the session ends or the process is interrupted.
        /// </summary>
        /// <returns>0 when the session was joined and the handshake accepted.</returns>
        public static int Main(string[] args)
        {
            Options options = Options.Parse(args);
            if (options.ShowHelp)
            {
                Options.PrintUsage();
                return 0;
            }

            if (!NetworkAccess.IsSupported)
            {
                Console.Error.WriteLine("This Drova Modding API build has no coop support. Build it with a coop configuration and point -p:ApiAssembly at it.");
                return 2;
            }

            Action pump = ResolveApiPump();

            _centerX = options.CenterX;
            _centerY = options.CenterY;
            _centerKnown = options.HasCenter;
            _positionX = _centerX;
            _positionY = _centerY;

            NetworkAccess.RegisterWithId<HandshakeMessage>(MessageIds.Handshake, OnHandshake);
            NetworkAccess.RegisterWithId<HandshakeResultMessage>(MessageIds.HandshakeResult, OnHandshakeResult);
            NetworkAccess.RegisterWithId<RosterSnapshotMessage>(MessageIds.RosterSnapshot, OnRosterSnapshot);
            NetworkAccess.RegisterWithId<PlayerJoinedMessage>(MessageIds.PlayerJoined, OnPlayerJoined);
            NetworkAccess.RegisterWithId<PlayerLeftMessage>(MessageIds.PlayerLeft, OnPlayerLeft);
            NetworkAccess.RegisterWithId<PlayerStateMessage>(MessageIds.PlayerState, OnPlayerState);

            NetworkEvents.OnHostStarted += OnHostStarted;
            NetworkEvents.OnConnectedToHost += OnConnectedToHost;
            NetworkEvents.OnPeerConnected += peer => Console.WriteLine($"peer {peer.Id} connected");
            NetworkEvents.OnPeerDisconnected += (peer, reason) => Console.WriteLine($"peer {peer.Id} left: {reason}");
            NetworkEvents.OnConnectionRejected += rejection => Console.WriteLine($"refused: {rejection}");
            NetworkEvents.OnNetworkError += error => Console.Error.WriteLine("transport error: " + error);

            if (options.Host)
            {
                Console.WriteLine($"hosting through {options.Relay}:{options.Port}...");
                NetworkAccess.StartHostViaRelay(options.Relay, options.Port, options.Code, options.Password);
            }
            else
            {
                Console.WriteLine($"joining '{options.Code}' through {options.Relay}:{options.Port} as '{options.Name}'...");
                NetworkAccess.ConnectViaRelay(options.Relay, options.Port, options.Code!, options.Password);
            }

            return Run(options, pump);
        }

        /// <summary>
        /// Pumps the transport at a fixed rate and sends state while the session lasts.
        ///
        /// A fixed rate rather than a spin, because the mod's own send rate currently follows the render
        /// framerate and that is a bug this tool should not reproduce: a peer sending as fast as it can
        /// would make every measurement taken against it meaningless.
        /// </summary>
        private static int Run(Options options, Action pump)
        {
            const int tickMs = 1000 / 30;

            using CancellationTokenSource cancellation = new();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            DateTime deadline = DateTime.UtcNow.AddSeconds(options.Seconds);
            DateTime lastReport = DateTime.UtcNow;
            const float tickSeconds = tickMs / 1000f;
            float angle = 0f;
            float phase = 0f;

            while (!cancellation.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                pump();

                if (options.Mirror && (_handshakeAccepted || options.Host))
                {
                    // Whatever the other player is doing, done a few units to the side. Nothing is sent
                    // until they have been seen at least once.
                    if (_mirrorSeen)
                    {
                        PlayerStateMessage mirrored = _mirrorOf;
                        mirrored.PlayerId = _localPlayerId;
                        mirrored.X += options.MirrorOffset;

                        NetworkAccess.SendToAll(mirrored, Delivery.Unreliable);
                    }
                }
                else if (options.Move && (_handshakeAccepted || options.Host))
                {
                    // A slow circle, so the other side sees something that is obviously this peer and
                    // obviously moving rather than a value that might just be a stuck packet.
                    // Nothing is sent until there is somewhere sensible to walk. Orbiting the origin while
                    // the other player is thousands of units away puts this peer's body out of sight, and
                    // looks exactly like the replication not working.
                    if (!_centerKnown) continue;

                    // Position is integrated from the speed this peer claims to be moving at, rather than
                    // being swept along a circle at whatever rate the send loop happens to run. The two
                    // have to agree: a peer that says "running" while its position crawls makes the far
                    // end correct a body mid-stride on every frame, which reads as a slow, blurred
                    // character and is a fault in the test rig rather than in the replication.
                    phase += tickSeconds;
                    bool standing = phase % (options.WalkSeconds + options.StandSeconds) >= options.WalkSeconds;

                    float directionX = 0f;
                    float directionY = 0f;

                    if (!standing)
                    {
                        angle += options.TurnRate * tickSeconds;
                        directionX = -(float)Math.Sin(angle);
                        directionY = (float)Math.Cos(angle);

                        _positionX += directionX * options.Speed * tickSeconds;
                        _positionY += directionY * options.Speed * tickSeconds;
                    }

                    PlayerStateMessage state = new()
                    {
                        PlayerId = _localPlayerId,
                        X = _positionX,
                        Y = _positionY,
                        DirectionX = directionX,
                        DirectionY = directionY,
                        LookX = directionX,
                        LookY = directionY,
                        MoveMode = standing ? MoveModeStand : MoveModeRun,
                    };

                    NetworkAccess.SendToAll(state, Delivery.Unreliable);
                }

                if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
                {
                    lastReport = DateTime.UtcNow;
                    Console.WriteLine($"peers={NetworkAccess.PeerCount} statesReceived={_statesReceived} " +
                        $"mirroring={_mirrorSeen}");
                }

                Thread.Sleep(tickMs);
            }

            NetworkAccess.Stop();
            Console.WriteLine($"done. handshakeAnswered={_handshakeAnswered} accepted={_handshakeAccepted} statesReceived={_statesReceived}");

            return options.Host || _handshakeAccepted ? 0 : 1;
        }

        private static void OnHostStarted()
        {
            // The host is player 0 and needs no handshake to learn it, same as the mod.
            _localPlayerId = PlayerId.Host;
            _players[PlayerId.Host] = Options.Current.Name;

            Console.WriteLine($"hosting, session code {NetworkAccess.SessionCode}, encrypted={NetworkAccess.IsEncrypted}, this peer is {_localPlayerId}");
        }

        private static void OnConnectedToHost()
        {
            HandshakeMessage handshake = new()
            {
                ProtocolVersion = CoopProtocol.Version,
                PlayerName = Options.Current.Name,
            };

            NetworkAccess.SendToAll(handshake);
            Console.WriteLine($"connected, sent handshake (protocol {CoopProtocol.Version})");
        }

        /// <summary>
        /// Answers a handshake when this peer is the host, so a real game can join it.
        /// </summary>
        private static void OnHandshake(INetPeer peer, HandshakeMessage message)
        {
            if (NetworkAccess.Role != NetRole.Host) return;
            if (_peerToPlayer.ContainsKey(peer.Id)) return;

            bool accepted = message.ProtocolVersion == CoopProtocol.Version;
            PlayerId assigned = accepted ? NextPlayerId() : PlayerId.None;

            if (accepted && !assigned.IsAssigned)
            {
                accepted = false;
            }

            Console.WriteLine($"handshake from peer {peer.Id} '{message.PlayerName}' protocol {message.ProtocolVersion}: " +
                (accepted ? $"accepted as {assigned}" : "refused"));

            HandshakeResultMessage result = new()
            {
                Accepted = accepted,
                HostProtocolVersion = CoopProtocol.Version,
                AssignedPlayerId = assigned,
                Refusal = accepted ? HandshakeRefusal.None
                    : message.ProtocolVersion == CoopProtocol.Version ? HandshakeRefusal.SessionFull
                    : HandshakeRefusal.ProtocolMismatch,
            };

            NetworkAccess.Send(peer, result);

            if (!accepted)
            {
                peer.Disconnect();
                return;
            }

            _peerToPlayer[peer.Id] = assigned;
            _players[assigned] = message.PlayerName ?? assigned.ToString();

            // The same pair the mod sends: everyone already in, to the newcomer; the newcomer, to everyone
            // already in. A peer that only sent one of these would leave somebody with a wrong roster.
            List<RosterEntry> entries = new();
            foreach (KeyValuePair<PlayerId, string> player in _players)
            {
                if (player.Key == assigned) continue;

                entries.Add(new RosterEntry(player.Key, player.Value));
            }

            NetworkAccess.Send(peer, new RosterSnapshotMessage { Players = entries.ToArray() });

            PlayerJoinedMessage joined = new() { Id = assigned, Name = _players[assigned] };
            SendToPeersExcept(joined, peer.Id);
        }

        private static void OnRosterSnapshot(INetPeer peer, RosterSnapshotMessage message)
        {
            foreach (RosterEntry entry in message.Players ?? [])
            {
                _players[entry.Id] = entry.Name;
            }

            Console.WriteLine($"roster: {string.Join(", ", (message.Players ?? []).Select(entry => $"{entry.Id} '{entry.Name}'"))}");
        }

        private static void OnPlayerJoined(INetPeer peer, PlayerJoinedMessage message)
        {
            _players[message.Id] = message.Name;
            Console.WriteLine($"{message.Id} '{message.Name}' joined");
        }

        private static void OnPlayerLeft(INetPeer peer, PlayerLeftMessage message)
        {
            _players.Remove(message.Id);
            Console.WriteLine($"{message.Id} left");
        }

        /// <summary>
        /// The lowest free client id, matching how the mod assigns them.
        /// </summary>
        private static PlayerId NextPlayerId()
        {
            for (byte candidate = 1; candidate <= CoopProtocol.MaxClients; candidate++)
            {
                PlayerId id = new(candidate);
                if (!_players.ContainsKey(id)) return id;
            }

            return PlayerId.None;
        }

        private static void SendToPeersExcept<T>(T message, int exceptPeerId, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            List<INetPeer> peers = new();
            NetworkAccess.GetPeers(peers);

            foreach (INetPeer other in peers)
            {
                if (other.Id == exceptPeerId) continue;

                NetworkAccess.Send(other, message, delivery);
            }
        }

        private static void OnHandshakeResult(INetPeer peer, HandshakeResultMessage message)
        {
            if (NetworkAccess.Role != NetRole.Client) return;

            _handshakeAnswered = true;
            _handshakeAccepted = message.Accepted;

            if (message.Accepted)
            {
                _localPlayerId = message.AssignedPlayerId;
                Console.WriteLine($"host accepted the handshake; this peer is {_localPlayerId}");
                return;
            }

            Console.WriteLine(message.Refusal switch
            {
                HandshakeRefusal.SessionFull => "host refused: the session is full",
                HandshakeRefusal.ProtocolMismatch =>
                    $"host refused: it runs coop protocol {message.HostProtocolVersion}, this peer runs {CoopProtocol.Version}",
                _ => "host refused without saying why",
            });
        }

        private static void OnPlayerState(INetPeer peer, PlayerStateMessage message)
        {
            _statesReceived++;

            _mirrorOf = message;
            _mirrorSeen = true;

            // The other side's speed, measured rather than assumed. It is the number --speed should be set
            // to, and nothing else here can find it out: the game's run speed lives in stats this tool
            // cannot see.
            if (_lastObservedTime > 0)
            {
                float elapsed = (float)(DateTime.UtcNow - _observedAt).TotalSeconds;
                if (elapsed > 0.001f)
                {
                    float moved = (float)Math.Sqrt(
                        Math.Pow(message.X - _lastObservedX, 2) + Math.Pow(message.Y - _lastObservedY, 2));

                    float speed = moved / elapsed;
                    if (speed > _peakObservedSpeed && speed < 1000f) _peakObservedSpeed = speed;
                }
            }

            _lastObservedX = message.X;
            _lastObservedY = message.Y;
            _observedAt = DateTime.UtcNow;
            _lastObservedTime = 1;

            // Walk around whoever is already out there, unless a center was asked for explicitly.
            if (!_centerKnown)
            {
                _centerX = message.X;
                _centerY = message.Y;
                _positionX = message.X;
                _positionY = message.Y;
                _centerKnown = true;
                Console.WriteLine($"circling ({_centerX:0.0}, {_centerY:0.0}), taken from {message.PlayerId}");
            }

            // Hosting means relaying, exactly as the mod does: stamp the sender's real id rather than the
            // one it claimed, and pass it to everyone except the sender.
            if (NetworkAccess.Role == NetRole.Host && _peerToPlayer.TryGetValue(peer.Id, out PlayerId sender))
            {
                message.PlayerId = sender;
                SendToPeersExcept(message, peer.Id, Delivery.Unreliable);
            }

            // Only the first few, then counted. A state arrives every frame per player, so printing them
            // all buries every other line in the output.
            if (_statesReceived <= 3)
            {
                Console.WriteLine($"state from peer {peer.Id}: at ({message.X:0.0}, {message.Y:0.0}) " +
                    $"pushing ({message.DirectionX:0.00}, {message.DirectionY:0.00}) mode {message.MoveMode}");
            }
        }

        /// <summary>
        /// Finds the transport's poll method. The API pumps it from its melon's update, which does not
        /// exist here, so this drives it directly. Private on purpose in the API - a mod has no business
        /// calling it - which is why this reaches for it by reflection and says so loudly if it moves.
        /// </summary>
        private static Action ResolveApiPump()
        {
            Type? system = typeof(NetworkAccess).Assembly.GetType("Drova_Modding_API.Systems.Networking.Impl.NetworkSystem");
            MethodInfo? poll = system?.GetMethod("Poll", BindingFlags.NonPublic | BindingFlags.Static);

            if (poll == null)
            {
                throw new InvalidOperationException("NetworkSystem.Poll not found; is this a coop-enabled API build?");
            }

            return (Action)Delegate.CreateDelegate(typeof(Action), poll);
        }
    }
}
