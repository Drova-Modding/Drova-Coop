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
        private static bool _handshakeAccepted;
        private static bool _handshakeAnswered;
        private static int _statesReceived;

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

            NetworkAccess.RegisterWithId<HandshakeMessage>(MessageIds.Handshake, OnHandshake);
            NetworkAccess.RegisterWithId<HandshakeResultMessage>(MessageIds.HandshakeResult, OnHandshakeResult);
            NetworkAccess.RegisterWithId<PlayerStateMessage>(MessageIds.PlayerState, OnPlayerState);

            NetworkEvents.OnHostStarted += () => Console.WriteLine($"hosting, session code {NetworkAccess.SessionCode}, encrypted={NetworkAccess.IsEncrypted}");
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
            float angle = 0f;

            while (!cancellation.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                pump();

                if (options.Move && (_handshakeAccepted || options.Host))
                {
                    // A slow circle, so the other side sees something that is obviously this peer and
                    // obviously moving rather than a value that might just be a stuck packet.
                    angle += 0.05f;
                    PlayerStateMessage state = new()
                    {
                        PlayerId = 0,
                        X = options.CentreX + (float)Math.Cos(angle) * options.Radius,
                        Y = options.CentreY + (float)Math.Sin(angle) * options.Radius,
                        Facing = angle * 57.2958f,
                    };

                    NetworkAccess.SendToAll(state, Delivery.Unreliable);
                }

                if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
                {
                    lastReport = DateTime.UtcNow;
                    Console.WriteLine($"peers={NetworkAccess.PeerCount} statesReceived={_statesReceived}");
                }

                Thread.Sleep(tickMs);
            }

            NetworkAccess.Stop();
            Console.WriteLine($"done. handshakeAnswered={_handshakeAnswered} accepted={_handshakeAccepted} statesReceived={_statesReceived}");

            return options.Host || _handshakeAccepted ? 0 : 1;
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

            bool accepted = message.ProtocolVersion == CoopProtocol.Version;
            Console.WriteLine($"handshake from peer {peer.Id} '{message.PlayerName}' protocol {message.ProtocolVersion}: {(accepted ? "accepted" : "refused")}");

            HandshakeResultMessage result = new()
            {
                Accepted = accepted,
                HostProtocolVersion = CoopProtocol.Version,
            };

            NetworkAccess.Send(peer, result);

            if (!accepted)
            {
                peer.Disconnect();
            }
        }

        private static void OnHandshakeResult(INetPeer peer, HandshakeResultMessage message)
        {
            if (NetworkAccess.Role != NetRole.Client) return;

            _handshakeAnswered = true;
            _handshakeAccepted = message.Accepted;

            Console.WriteLine(message.Accepted
                ? "host accepted the handshake"
                : $"host refused: it runs coop protocol {message.HostProtocolVersion}, this peer runs {CoopProtocol.Version}");
        }

        private static void OnPlayerState(INetPeer peer, PlayerStateMessage message)
        {
            _statesReceived++;

            // Only the first few, then counted. A state arrives every frame per player, so printing them
            // all buries every other line in the output.
            if (_statesReceived <= 3)
            {
                Console.WriteLine($"state from peer {peer.Id}: ({message.X:0.0}, {message.Y:0.0}) facing {message.Facing:0.0}");
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
