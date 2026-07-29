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

        // Last reported values from the other side, so a line is printed when one moves rather than
        // thirty times a second while nothing does.
        private static ushort _observedHealth = ushort.MaxValue;
        private static ushort _observedStamina = ushort.MaxValue;
        private static byte _observedDowned = byte.MaxValue;

        private static int _announcedToPeers;
        private static bool _warnedAboutCenter;
        private static int _combatPhaseIndex = -1;
        private static byte _combatSequence;
        private static byte _combatLastAction;

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
            NetworkAccess.RegisterWithId<PingMessage>(MessageIds.Ping, OnPing);

            // Registered even though this peer has nothing to do with either. An id that arrives
            // unregistered is a dispatch failure at the far end rather than a message politely ignored,
            // and a test rig that cannot receive half the protocol is a test rig that reports faults
            // which are its own.
            NetworkAccess.RegisterWithId<PlayerEquipmentMessage>(MessageIds.PlayerEquipment, OnEquipment);
            NetworkAccess.RegisterWithId<SessionRulesMessage>(MessageIds.SessionRules, OnSessionRules);
            NetworkAccess.RegisterWithId<DamageReportMessage>(MessageIds.DamageReport, OnDamageReport);
            NetworkAccess.RegisterWithId<ItemUseMessage>(MessageIds.ItemUse, OnItemUse);
            NetworkAccess.RegisterWithId<StatusEffectMessage>(MessageIds.StatusEffect, OnStatusEffect);
            NetworkAccess.RegisterWithId<KnowledgeMessage>(MessageIds.Knowledge, OnKnowledge);

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
            // How often state is sent. Matches what the mod sends at, so measurements taken against this
            // peer describe the mod rather than the tool.
            const int tickMs = 1000 / 30;

            // How often the socket is read, which is a different question. A packet that has arrived but
            // has not been looked at is just as late as one still in flight, so polling only when there is
            // something to send would add up to a send interval of latency to every reply this peer makes
            // - and a round trip measured against it would report the tool's sleep, not the link.
            const int pumpMs = 2;

            using CancellationTokenSource cancellation = new();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            DateTime deadline = DateTime.UtcNow.AddSeconds(options.Seconds);
            DateTime lastReport = DateTime.UtcNow;
            DateTime startedAt = DateTime.UtcNow;
            const float tickSeconds = tickMs / 1000f;
            float angle = 0f;
            float phase = 0f;

            while (!cancellation.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                pump();

                bool inSession = _handshakeAccepted || options.Host;

                // Re-announced whenever somebody new turns up, not once for the session. Equipment is
                // reliable and sent on change, so anyone who joined after the one announcement would
                // never hear it - and "the second player to join has no shield" is a fault in the rig
                // that reads exactly like a fault in the mod.
                if (inSession && options.Equip != null && NetworkAccess.PeerCount > _announcedToPeers)
                {
                    _announcedToPeers = NetworkAccess.PeerCount;

                    NetworkAccess.SendToAll(new PlayerEquipmentMessage
                    {
                        PlayerId = _localPlayerId,
                        ItemIds = options.Equip,
                        BodyType = options.BodyType,
                        VoiceId = options.VoiceId,
                    });

                    Console.WriteLine($"announced equipment [{string.Join(", ", options.Equip)}] to {_announcedToPeers} peer(s)");
                }

                // Says why nothing is happening, once. Walking and fighting both refuse to start until
                // there is somewhere sensible to stand, and a peer that silently sends nothing is
                // indistinguishable from replication that does not work - which is the one thing this
                // tool exists to rule out.
                if (inSession && !_centerKnown && !_warnedAboutCenter && (options.Move || options.Combat)
                    && DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(5))
                {
                    _warnedAboutCenter = true;
                    Console.WriteLine("nothing sent yet: no position to act at. Pass --center-x/--center-y, " +
                        "or wait for the other side to send its own state.");
                }

                if (options.Mirror && inSession)
                {
                    // Whatever the other player is doing, done a few units to the side. Nothing is sent
                    // until they have been seen at least once.
                    if (_mirrorSeen)
                    {
                        PlayerStateMessage mirrored = _mirrorOf;
                        mirrored.PlayerId = _localPlayerId;
                        mirrored.X += options.MirrorOffset;

                        // Layered on top of the mirror rather than instead of it, so the fight happens
                        // wherever the other player is standing and stays in view.
                        if (options.Combat)
                        {
                            phase += tickSeconds;
                            ApplyCombat(ref mirrored, phase, options.CombatSeconds);
                        }

                        NetworkAccess.SendToAll(mirrored, Delivery.Unreliable);
                    }
                }
                else if ((options.Move || options.Combat) && inSession)
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

                    // Fighting on the spot when only --combat was asked for. A body that walks off while
                    // demonstrating a block is harder to watch, not easier.
                    bool standing = !options.Move
                        || phase % (options.WalkSeconds + options.StandSeconds) >= options.WalkSeconds;

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

                    // Last, so the fight owns the stance and the action while the walk owns the position.
                    // The two are independent on the wire and have to be independent here, or a body that
                    // blocks while walking is never tested.
                    if (options.Combat)
                    {
                        ApplyCombat(ref state, phase, options.CombatSeconds);
                    }

                    NetworkAccess.SendToAll(state, Delivery.Unreliable);
                }

                if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(5))
                {
                    lastReport = DateTime.UtcNow;
                    Console.WriteLine($"peers={NetworkAccess.PeerCount} statesReceived={_statesReceived} " +
                        $"mirroring={_mirrorSeen}");
                }

                // Slept in slices, pumping each one, rather than sleeping the whole send interval in one
                // go. The constant above says why this matters and the loop used not to honour it: a
                // reply that waits for the next send tick is up to a full interval late, so a round trip
                // measured against this peer reported the tool's own sleep rather than the link. That is
                // no longer only cosmetic - the mod now slides a remote body's perfect-block window by
                // the measured round trip, so an inflated number here moves a window in the game.
                for (int slept = 0; slept < tickMs; slept += pumpMs)
                {
                    Thread.Sleep(pumpMs);
                    pump();
                }
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

                // The host decides this for the session, and here the host has no options window to read
                // it from. Without the flag a peer-hosted session is always friendly-fire-off, which is
                // exactly the setting under which none of the combat replication can be observed.
                FriendlyFire = Options.Current.FriendlyFire,
                RecoveryHealthPercent = Options.Current.RecoveryHealthPercent,
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

        /// <summary>
        /// Answers pings so the other side can measure the round trip.
        ///
        /// Without this the game shows "measuring" forever, which looks like a fault in its own ping code
        /// rather than a peer that never replied.
        /// </summary>
        private static void OnPing(INetPeer peer, PingMessage message)
        {
            if (message.IsReply) return;

            PingMessage reply = new()
            {
                Sender = _localPlayerId,
                Token = message.Token,
                IsReply = true,
            };

            NetworkAccess.Send(peer, reply, Delivery.Unreliable);
        }

        /// <summary>
        /// One phase of the scripted fight, and what the state message should say during it.
        /// </summary>
        /// <param name="Label">What to print when the phase begins.</param>
        /// <param name="Share">How much of one pass this phase takes.</param>
        /// <param name="Stance">The stance to claim, as <c>Actor.EInteractionMode</c>.</param>
        /// <param name="Action">The action to claim, as <c>Actor.ActionModule.EActionBind</c>.</param>
        /// <param name="Released">Whether the button is reported as let go of.</param>
        /// <param name="Downed">Whether this phase reports the player on the floor.</param>
        private readonly record struct CombatPhase(
            string Label, float Share, byte Stance, byte Action, bool Released, bool Downed);

        /// <summary>
        /// <c>Actor.EInteractionMode</c>, hardcoded for the same reason the move modes above are: the
        /// enum belongs to the game and this tool builds on machines that do not have it.
        /// </summary>
        private const byte StanceNormal = 0;
        private const byte StanceCombat = 1;
        private const byte StanceKnockOut = 4;

        /// <summary><c>Actor.ActionModule.EActionBind</c>.</summary>
        private const byte ActionNone = 0;
        private const byte ActionPrimary = 1;
        private const byte ActionSecondary = 2;
        private const byte ActionDodge = 3;

        /// <summary>
        /// One pass through everything a player's state can say, in the order a fight would say it.
        ///
        /// The shares are what makes each phase testable rather than a flicker. A tap and a charge differ
        /// only in when the release flag flips - that distinction is the one that made every ordinary
        /// attack replicate as a charged one, so it gets two phases rather than a comment. The block is
        /// the longest, because it is the phase whose effects are computed on the far side and need time
        /// to be hit during.
        /// </summary>
        private static readonly CombatPhase[] _combatScript =
        [
            new("stance: weapon away", 0.10f, StanceNormal, ActionNone, true, false),
            new("stance: weapon drawn", 0.10f, StanceCombat, ActionNone, true, false),
            new("attack: tap", 0.08f, StanceCombat, ActionPrimary, true, false),
            new("recover", 0.07f, StanceCombat, ActionNone, true, false),
            new("attack: charged (button held)", 0.12f, StanceCombat, ActionPrimary, false, false),
            new("attack: charged (released)", 0.05f, StanceCombat, ActionPrimary, true, false),
            new("dodge", 0.08f, StanceCombat, ActionDodge, true, false),
            new("block: holding", 0.20f, StanceCombat, ActionSecondary, false, false),
            new("block: released", 0.05f, StanceCombat, ActionSecondary, true, false),
            new("down", 0.10f, StanceKnockOut, ActionNone, true, true),
            new("back up", 0.05f, StanceCombat, ActionNone, true, false),
        ];

        /// <summary>
        /// Fills in what the fight loop is doing at this point in a pass, and bumps the action sequence
        /// whenever the action changes - which is the same rule the mod's own broadcaster follows, and
        /// the reason two swings in a row are two swings rather than one long one at the far end.
        /// </summary>
        private static void ApplyCombat(ref PlayerStateMessage state, float phase, float passSeconds)
        {
            float position = phase % passSeconds / passSeconds;
            float walked = 0f;
            CombatPhase current = _combatScript[^1];
            int index = _combatScript.Length - 1;

            for (int i = 0; i < _combatScript.Length; i++)
            {
                walked += _combatScript[i].Share;
                if (position >= walked) continue;

                current = _combatScript[i];
                index = i;
                break;
            }

            if (index != _combatPhaseIndex)
            {
                _combatPhaseIndex = index;
                Console.WriteLine("combat: " + current.Label);
            }

            state.InteractionMode = current.Stance;
            state.Action = current.Action;
            state.ActionReleased = current.Released ? (byte)1 : (byte)0;
            state.Downed = current.Downed ? (byte)1 : (byte)0;

            // The sequence answers "is this a new use", and only the action identity decides that - not
            // the release. Bumping it on release would make every tap arrive as two swings, which is a
            // bug this project has already shipped once.
            if (current.Action != _combatLastAction)
            {
                _combatLastAction = current.Action;

                if (current.Action != ActionNone)
                {
                    _combatSequence = unchecked((byte)(_combatSequence + 1));
                }
            }

            state.ActionSequence = _combatSequence;

            // Health and stamina are invented here, but they have to move: a body whose numbers never
            // change cannot show whether the far end applies them, and a stamina that never falls cannot
            // show a guard breaking.
            state.MaxHealth = 100;
            state.Health = current.Downed ? (ushort)2 : (ushort)80;
            state.MaxStamina = 100;
            state.Stamina = current.Action == ActionNone ? (ushort)90 : (ushort)25;
        }

        private static void OnEquipment(INetPeer peer, PlayerEquipmentMessage message)
        {
            Console.WriteLine($"{message.PlayerId} wears [{string.Join(", ", message.ItemIds ?? [])}] " +
                $"body={message.BodyType} voice={message.VoiceId}");
        }

        /// <summary>
        /// Prints what other players learned, and passes it on when hosting. Unlike everything else here
        /// this reaches everybody, so a host that did not relay it would leave a third player untaught.
        /// </summary>
        private static void OnKnowledge(INetPeer peer, KnowledgeMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Learner = sender;

                List<INetPeer> peers = new();
                NetworkAccess.GetPeers(peers);

                foreach (INetPeer other in peers)
                {
                    if (other.Id == peer.Id) continue;

                    NetworkAccess.Send(other, message, Delivery.Reliable);
                }
            }

            string kind = message.Kind switch
            {
                KnowledgeMessage.KindRecipe => "recipe",
                KnowledgeMessage.KindWriting => "writing",
                _ => "unknown(" + message.Kind + ")",
            };

            Console.WriteLine($"{message.Learner} learned {kind} '{message.Id}'");
        }

        /// <summary>
        /// Prints the status effects other players report, and routes them when hosting. Without this the
        /// rig would drop them silently, which looks exactly like an effect that was never sent.
        /// </summary>
        private static void OnStatusEffect(INetPeer peer, StatusEffectMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                // The same substitution the mod makes: the sender does not get to name the attacker.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Source = sender;

                if (message.Target != _localPlayerId)
                {
                    SendToPeer(message, message.Target);
                    return;
                }
            }

            Console.WriteLine($"{message.Source} put '{message.EffectId}' on {message.Target} (effect #{message.Sequence})");
        }

        /// <summary>
        /// Prints the casts other players announce, which is what makes a spell that never reached the
        /// wire distinguishable from one that reached it and was not performed.
        /// </summary>
        private static void OnItemUse(INetPeer peer, ItemUseMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                // The same substitution the mod makes: a sender does not get to cast as somebody else.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.User = sender;
            }

            string edge = message.Released != 0 ? "released" : "started";

            Console.WriteLine($"{message.User} {edge} '{message.ItemId}' (cast #{message.Sequence})");
        }

        private static void OnSessionRules(INetPeer peer, SessionRulesMessage message)
        {
            Console.WriteLine($"session rules changed: friendlyFire={message.FriendlyFire}");
        }

        /// <summary>
        /// Reports a blow, and routes it when this peer is the host.
        ///
        /// Printing it is the point: a report reaching here proves the whole path - the game calculated a
        /// swing against a body it cannot hurt and addressed the result to the player it belongs to - and
        /// it does so without a second copy of Drova to be hit in. Nothing is applied, because this peer
        /// has no health to take it off; the figure on screen is the test.
        /// </summary>
        private static void OnDamageReport(INetPeer peer, DamageReportMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                // Same substitution the mod makes, for the same reason: the sender does not get to name
                // the attacker. A rig that skipped it would pass traffic the real host would reject.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Attacker = sender;

                if (message.Victim != _localPlayerId)
                {
                    SendToPeer(message, message.Victim);
                    return;
                }
            }

            string extra = message.PoiseDamage > 0f ? $", {message.PoiseDamage:0.#} poise" : string.Empty;
            string wound = message.AppliesBleed != 0 ? ", opens a wound" : string.Empty;

            Console.WriteLine($"{message.Attacker} hit {message.Victim} for {message.Amount}{extra}{wound} (blow #{message.Sequence})");
        }

        /// <summary>
        /// Sends to one player by session id, which only a host can do - a client cannot translate a player
        /// id into a peer.
        /// </summary>
        private static void SendToPeer<T>(T message, PlayerId player, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            List<INetPeer> peers = new();
            NetworkAccess.GetPeers(peers);

            foreach (INetPeer candidate in peers)
            {
                if (!_peerToPlayer.TryGetValue(candidate.Id, out PlayerId mapped) || mapped != player) continue;

                NetworkAccess.Send(candidate, message, delivery);
                return;
            }
        }

        private static void OnPlayerState(INetPeer peer, PlayerStateMessage message)
        {
            _statesReceived++;

            // Reported rather than only counted, because these are the fields whose whole purpose is to
            // be read on the far side. A peer that says "statesReceived=900" proves packets arrive; it
            // does not prove the game filled in the health, the stamina or the knockout.
            if (message.Health != _observedHealth || message.Stamina != _observedStamina || message.Downed != _observedDowned)
            {
                _observedHealth = message.Health;
                _observedStamina = message.Stamina;
                _observedDowned = message.Downed;

                Console.WriteLine($"{message.PlayerId}: health {message.Health}/{message.MaxHealth} " +
                    $"stamina {message.Stamina}/{message.MaxStamina}{(message.Downed != 0 ? " DOWN" : "")}");
            }

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
