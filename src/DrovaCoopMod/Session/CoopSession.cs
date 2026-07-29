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

        private static readonly Dictionary<PlayerId, string> _players = new();
        private static readonly Dictionary<PlayerId, PlayerStateMessage> _latestStates = new();
        private static readonly Dictionary<PlayerId, EquipmentSnapshot> _equipment = new();
        private static readonly Dictionary<PlayerId, ItemUseSnapshot> _itemUses = new();

        // Host only. A transport peer id means nothing on another machine, so the host is the only thing
        // that can translate between the two, and it is the only thing that ever needs to.
        private static readonly Dictionary<int, PlayerId> _peerToPlayer = new();

        // Reused so relaying state does not allocate a list per player per frame.
        private static readonly List<INetPeer> _peerBuffer = new();

        private static readonly Dictionary<PlayerId, float> _roundTripMs = new();
        private static readonly Dictionary<uint, double> _pingSentAt = new();
        private static readonly List<uint> _expiredPings = new();

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

        // A real clock, not accumulated frame time. Timing a round trip by adding up per-frame deltas
        // measures in whole frames: every result comes back a multiple of the frame duration, so a 60 fps
        // game cannot report anything between 17 ms and 33 ms and the number describes the framerate more
        // than the link. The reply is still only *noticed* on a frame boundary, because that is when the
        // transport is pumped, but that adds under one frame instead of quantizing the whole measurement.
        private static readonly System.Diagnostics.Stopwatch _clock = new();

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

        // State messages refused for carrying numbers no body can be given. Kept as a tally because the
        // path is reachable by any peer in the session: a line per packet is how somebody else's disk
        // gets filled. Non-zero here is either a peer sending nonsense or a bug on the sending side, and
        // either way the count is what says so.
        private static int _implausibleStates;

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
        /// Why the last attempt failed, in words meant for a player. Null unless
        /// <see cref="State"/> is <see cref="CoopSessionState.Failed"/>.
        /// </summary>
        public static string? LastError { get; private set; }

        /// <summary>
        /// How many state updates this session has refused for carrying numbers no body can be given -
        /// a NaN or an absurd coordinate. Zero on a healthy session; anything else is a peer sending
        /// nonsense, whether through a bug or on purpose, and this is where that becomes visible.
        /// </summary>
        public static int ImplausibleStates => _implausibleStates;

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
            NetworkAccess.RegisterWithId<SessionRulesMessage>(MessageIds.SessionRules, OnSessionRules);
            NetworkAccess.RegisterWithId<PlayerStateMessage>(MessageIds.PlayerState, OnPlayerState);
            NetworkAccess.RegisterWithId<PingMessage>(MessageIds.Ping, OnPing);
            NetworkAccess.RegisterWithId<PlayerEquipmentMessage>(MessageIds.PlayerEquipment, OnEquipment);
            NetworkAccess.RegisterWithId<DamageReportMessage>(MessageIds.DamageReport, OnDamageReport);
            NetworkAccess.RegisterWithId<ItemUseMessage>(MessageIds.ItemUse, OnItemUse);
            NetworkAccess.RegisterWithId<StatusEffectMessage>(MessageIds.StatusEffect, OnStatusEffect);
            NetworkAccess.RegisterWithId<KnowledgeMessage>(MessageIds.Knowledge, OnKnowledge);

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
            if (State == CoopSessionState.Hosting || State == CoopSessionState.Joined)
            {
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

            _expiredPings.Clear();
            foreach (KeyValuePair<uint, double> pending in _pingSentAt)
            {
                if (now - pending.Value > PingExpirySeconds * 1000d)
                {
                    _expiredPings.Add(pending.Key);
                }
            }

            foreach (uint expired in _expiredPings)
            {
                _pingSentAt.Remove(expired);
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
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;

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

            _roundTripMs[message.Sender] = (float)(_clock.Elapsed.TotalMilliseconds - sentAt);
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
            NetworkAccess.Unregister<PingMessage>();
            NetworkAccess.Unregister<PlayerEquipmentMessage>();
            NetworkAccess.Unregister<DamageReportMessage>();
            NetworkAccess.Unregister<ItemUseMessage>();
            NetworkAccess.Unregister<StatusEffectMessage>();
            NetworkAccess.Unregister<KnowledgeMessage>();

            _initialized = false;
        }

        /// <summary>
        /// Sends the local player's state to everyone. Unreliable by design: the next one supersedes it.
        /// </summary>
        /// <param name="message">The state to send. The caller fills in the intent; this stamps the id,
        /// so no caller can send state claiming to be somebody else even by accident.</param>
        public static void BroadcastPlayerState(PlayerStateMessage message)
        {
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;

            message.PlayerId = LocalPlayerId;
            NetworkAccess.SendToAll(message, Delivery.Unreliable);
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
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;

            PlayerEquipmentMessage message = new()
            {
                PlayerId = LocalPlayerId,
                ItemIds = itemIds,
                ItemSets = itemSets,
                ActiveSet = activeSet,
                BodyType = bodyType,
                VoiceId = voiceId,
            };

            NetworkAccess.SendToAll(message);
        }

        private static void OnEquipment(INetPeer peer, PlayerEquipmentMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.PlayerId = sender;
                Remember(sender, message);

                SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!IsFromHost()) return;
            if (message.PlayerId == LocalPlayerId) return;

            Remember(message.PlayerId, message);
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

        // ------------------------------------------------------------------ abilities

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
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;
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

        // ------------------------------------------------------------------ damage

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
        public static void ReportDamage(PlayerId victim, int amount, float poiseDamage, bool appliesBleed, byte damageType)
        {
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;

            // Checked here as well as on arrival. A blow struck in the frame the host turned the rule off
            // should not land, and this is the earlier of the two chances to say so.
            if (!FriendlyFire) return;

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
            };

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
        {
            if (_incomingDamage.Count == 0)
            {
                report = default;
                return false;
            }

            report = _incomingDamage.Dequeue();
            return true;
        }

        private static void OnDamageReport(INetPeer peer, DamageReportMessage message)
        {
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;

            // The host's rule, applied by the machine that will take the damage. A peer running a build
            // that never asked is refused here rather than trusted to have checked.
            if (!FriendlyFire) return;
            if (message.Amount == 0) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                // The sender does not get to say who swung. Without this a client could report damage in
                // somebody else's name, which is worse than lying about its own: the victim would see a
                // fight with a player who never touched them.
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Attacker = sender;

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

            if (_incomingDamage.Count >= MaxPendingDamage)
            {
                _incomingDamage.Dequeue();
            }

            _incomingDamage.Enqueue(report);
        }

        // ------------------------------------------------------------------ knowledge

        /// <summary>
        /// Tells everyone that the local player learned something they should all know.
        ///
        /// **Reaches everybody, in both directions**, which nothing else here does. A recipe is not
        /// something one player did to another; it is a fact about the world that happens to have been
        /// discovered by whoever got to the paper first, and the paper is gone afterwards.
        /// </summary>
        /// <param name="kind">Which sort of thing was learned.</param>
        /// <param name="id">What was learned, in whatever form that kind is named by.</param>
        public static void BroadcastKnowledge(byte kind, string id)
        {
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;
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
        {
            if (_incomingKnowledge.Count == 0)
            {
                message = default;
                return false;
            }

            message = _incomingKnowledge.Dequeue();
            return true;
        }

        private static void OnKnowledge(INetPeer peer, KnowledgeMessage message)
        {
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Learner = sender;

                // Passed on before it is applied here, so a third player learns it at the same time the
                // host does rather than waiting for the host's world to be ready to teach it.
                SendToClientsExcept(message, peer.Id);
                Enqueue(message);
                return;
            }

            if (!IsFromHost()) return;
            if (message.Learner == LocalPlayerId) return;

            Enqueue(message);
        }

        /// <summary>
        /// Records something to teach the local player.
        ///
        /// **The oldest is kept, not dropped, which is the opposite of what the damage queue does.** A
        /// blow that cannot be applied is superseded by the next one, so discarding the front of that
        /// queue costs nothing worth having. Knowledge is not like that: nothing repeats it. A recipe
        /// dropped here is one this player can never learn, because the paper it came on was consumed on
        /// somebody else's machine, and no later message will mention it again.
        ///
        /// So the cap exists only to stop a hostile peer growing this without bound, and it is set where
        /// play will never reach it - anything at that depth is a flood or a session that has stopped
        /// draining, and either way it is worth a line rather than a silent loss.
        /// </summary>
        private static void Enqueue(in KnowledgeMessage message)
        {
            if (_incomingKnowledge.Count >= MaxPendingKnowledge)
            {
                MelonLogger.Warning($"[Know] more than {MaxPendingKnowledge} things are waiting to be learned; " +
                    $"'{message.Id}' from {message.Learner} was refused and is lost.");
                return;
            }

            _incomingKnowledge.Enqueue(message);
        }

        // ------------------------------------------------------------------ status effects

        /// <summary>
        /// Tells a player that this machine put a status effect on their body.
        ///
        /// Reliable, and sent once per application rather than continuously: an effect is something that
        /// happened, not a value being sampled, so a lost one is a poison nobody ever feels.
        /// </summary>
        /// <param name="target">Whose body took it.</param>
        /// <param name="effectId">The guid of the status effect asset.</param>
        public static void ReportStatusEffect(PlayerId target, string effectId)
        {
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;

            // The same rule the damage takes. An effect applied by a player is a hostile act, and a
            // session with friendly fire off should not be able to poison anyone through the side door.
            if (!FriendlyFire) return;

            if (!target.IsAssigned || target == LocalPlayerId) return;
            if (string.IsNullOrEmpty(effectId)) return;

            StatusEffectMessage message = new()
            {
                Target = target,
                Source = LocalPlayerId,
                EffectId = effectId,
                Sequence = NextStatusSequenceFor(target),
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
        {
            if (_incomingEffects.Count == 0)
            {
                message = default;
                return false;
            }

            message = _incomingEffects.Dequeue();
            return true;
        }

        private static void OnStatusEffect(INetPeer peer, StatusEffectMessage message)
        {
            if (State != CoopSessionState.Hosting && State != CoopSessionState.Joined) return;
            if (!FriendlyFire) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!_peerToPlayer.TryGetValue(peer.Id, out PlayerId sender)) return;

                message.Source = sender;

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

            if (_incomingEffects.Count >= MaxPendingDamage)
            {
                _incomingEffects.Dequeue();
            }

            _incomingEffects.Enqueue(message);
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
            _equipment.Clear();
            _itemUses.Clear();
            _peerToPlayer.Clear();
            _roundTripMs.Clear();
            _pingSentAt.Clear();
            _incomingDamage.Clear();
            _incomingEffects.Clear();
            _incomingKnowledge.Clear();
            _lastStatusSequence.Clear();
            _lastDamageSequence.Clear();
            _nextDamageSequence = 0;
            _nextStatusSequence.Clear();
            _clock.Reset();
            LocalPlayerId = PlayerId.None;
            _connectingSeconds = 0f;
            _implausibleStates = 0;
            _sincePing = 0f;

            // Back to the safe answers rather than left on whatever the last session decided. Anything
            // that builds a body consults these, and outside a session there is no host to have an
            // opinion.
            AdoptRules(false, CoopProtocol.DefaultRecoveryHealthPercent);
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

            // The host's settings are the session's rules from this moment on. TickRules keeps the two in
            // step for as long as the session lasts; this is only where they first meet.
            AdoptRules(CoopPreferences.FriendlyFire, CoopPreferences.RecoveryHealthPercent);

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
                    _players.Remove(departed);
                    _latestStates.Remove(departed);
                    _equipment.Remove(departed);
                    _itemUses.Remove(departed);

                    // Forgotten with them, because an id is reused as soon as it is free and the next
                    // player to hold it starts counting blows from one again. A high-water mark left
                    // behind would swallow every hit they land until they caught up with a stranger.
                    _lastDamageSequence.Remove(departed);
                    _lastStatusSequence.Remove(departed);

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

        // ------------------------------------------------------------------ session rules

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

            if (friendlyFire == FriendlyFire && recovery == RecoveryHealthPercent) return;

            AdoptRules(friendlyFire, recovery);

            // The whole set, not the field that moved. A rule message that carried only a change would
            // have to arrive, and arrive in order, to leave the far end correct; one that carries
            // everything is right on its own whenever it lands.
            NetworkAccess.SendToAll(new SessionRulesMessage
            {
                FriendlyFire = FriendlyFire,
                RecoveryHealthPercent = RecoveryHealthPercent,
            });
        }

        private static void OnSessionRules(INetPeer peer, SessionRulesMessage message)
        {
            // Only the host sets rules. Without this a host would apply a client's message and let any
            // peer decide who in the session can be killed.
            if (NetworkAccess.Role != NetRole.Client) return;
            if (State != CoopSessionState.Joined) return;

            AdoptRules(message.FriendlyFire, message.RecoveryHealthPercent);
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
        private static void AdoptRules(bool friendlyFire, byte recoveryHealthPercent)
        {
            if (recoveryHealthPercent == 0)
            {
                recoveryHealthPercent = CoopProtocol.DefaultRecoveryHealthPercent;
            }

            if (recoveryHealthPercent > 100)
            {
                recoveryHealthPercent = 100;
            }

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
                RecoveryHealthPercent = RecoveryHealthPercent,

                // The value latched when hosting started, not the setting as it reads now. Re-reading here
                // would hand a late joiner a different rule than the players already in the session, which
                // is exactly the asymmetry the setting travelling with the handshake exists to prevent.
                FriendlyFire = CoopSession.FriendlyFire,
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
            _players[LocalPlayerId] = Sanitize(PlayerIdentity.LocalName(), LocalPlayerId);

            // Adopted rather than read locally: these are the host's session rules, and a client that
            // kept its own answers could be a legal target for someone it could not hit back, or stand
            // up with more health than the player who put it down.
            AdoptRules(message.FriendlyFire, message.RecoveryHealthPercent);

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
            MelonLogger.Msg($"[Coop] {_players[message.Id]} joined as {message.Id} ({_players.Count} in session).");
        }

        private static void OnPlayerLeft(INetPeer peer, PlayerLeftMessage message)
        {
            if (!IsFromHost()) return;

            _players.Remove(message.Id);
            _latestStates.Remove(message.Id);
            _equipment.Remove(message.Id);
            _itemUses.Remove(message.Id);
            _lastDamageSequence.Remove(message.Id);
            _lastStatusSequence.Remove(message.Id);
            MelonLogger.Msg($"[Coop] {message.Id} left ({_players.Count} in session).");
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
                _implausibleStates++;
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
