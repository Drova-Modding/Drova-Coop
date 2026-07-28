using System.Reflection;
using Drova_Modding_API.Systems.Networking;
using Xunit;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// What the wire contract has to hold to: fields come back in the order they went out, and a name from
    /// a stranger is bounded no matter what the sender claims.
    /// </summary>
    public class MessageTests
    {
        [Fact]
        public void Handshake_RoundTripsVersionAndName()
        {
            HandshakeMessage sent = new()
            {
                ProtocolVersion = 7,
                PlayerName = "Nemeton",
                ModSetHash = 0xDEADBEEF,
            };

            HandshakeMessage received = RoundTrip(sent);

            Assert.Equal(7, received.ProtocolVersion);
            Assert.Equal("Nemeton", received.PlayerName);
            Assert.Equal(0xDEADBEEF, received.ModSetHash);
        }

        [Fact]
        public void Handshake_TruncatesOverlongNameOnWrite()
        {
            HandshakeMessage sent = new()
            {
                ProtocolVersion = 1,
                PlayerName = new string('n', HandshakeMessage.MaxPlayerNameLength + 200),
            };

            HandshakeMessage received = RoundTrip(sent);

            Assert.Equal(HandshakeMessage.MaxPlayerNameLength, received.PlayerName.Length);
        }

        /// <summary>
        /// The case that matters, because it is the one an attacker controls: a sender that ignores the cap
        /// and writes the field by hand. The receiver has to apply the bound itself rather than trusting
        /// that the other side already did.
        /// </summary>
        [Fact]
        public void Handshake_TruncatesOverlongNameOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put(CoopProtocol.Version);
            writer.Put(new string('n', 900));
            writer.Put(0u);

            stream.Position = 0;
            HandshakeMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(HandshakeMessage.MaxPlayerNameLength, received.PlayerName.Length);
        }

        [Fact]
        public void Handshake_WritesNullNameAsEmpty()
        {
            HandshakeMessage sent = new()
            {
                ProtocolVersion = 1,
                PlayerName = null!,
            };

            HandshakeMessage received = RoundTrip(sent);

            Assert.Equal(string.Empty, received.PlayerName);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HandshakeResult_RoundTrips(bool accepted)
        {
            HandshakeResultMessage sent = new()
            {
                Accepted = accepted,
                HostProtocolVersion = 42,
                AssignedPlayerId = accepted ? new PlayerId(3) : PlayerId.None,
                Refusal = accepted ? HandshakeRefusal.None : HandshakeRefusal.SessionFull,
            };

            HandshakeResultMessage received = RoundTrip(sent);

            Assert.Equal(accepted, received.Accepted);
            Assert.Equal(42, received.HostProtocolVersion);
            Assert.Equal(sent.AssignedPlayerId, received.AssignedPlayerId);
            Assert.Equal(sent.Refusal, received.Refusal);
        }

        /// <summary>
        /// The session rules the host hands down, each given the value the other does not have. They are
        /// adjacent booleans, so equal ones would survive a Read that swapped them - and the swap is not
        /// cosmetic here: it would turn "we run different mods" into "you may be killed by your friend".
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void HandshakeResult_RoundTripsSessionRulesIndependently(bool modSetMatches, bool friendlyFire)
        {
            HandshakeResultMessage received = RoundTrip(new HandshakeResultMessage
            {
                Accepted = true,
                ModSetMatches = modSetMatches,
                FriendlyFire = friendlyFire,
            });

            Assert.Equal(modSetMatches, received.ModSetMatches);
            Assert.Equal(friendlyFire, received.FriendlyFire);
        }

        /// <summary>
        /// The default has to be the safe one. A peer whose message never set the field - an older build,
        /// a truncated read, a default-constructed message - must not have friendly fire switched on by
        /// omission, because that failure is silent until somebody is killed by a friend.
        /// </summary>
        [Fact]
        public void HandshakeResult_DefaultsFriendlyFireOff()
        {
            Assert.False(default(HandshakeResultMessage).FriendlyFire);
            Assert.False(RoundTrip(new HandshakeResultMessage { Accepted = true }).FriendlyFire);
        }

        /// <summary>
        /// The mid-session rule change carries the same answer the handshake does, in both directions.
        /// Two messages state this rule, and they are read by different code paths on different machines;
        /// a session where the door and the update disagree is one where a player can be killed by
        /// somebody they were told could not hurt them.
        /// </summary>
        [Theory]
        [InlineData(true, 5)]
        [InlineData(false, 100)]
        public void SessionRules_RoundTripsEveryRule(bool friendlyFire, byte recovery)
        {
            SessionRulesMessage received = RoundTrip(new SessionRulesMessage
            {
                FriendlyFire = friendlyFire,
                RecoveryHealthPercent = recovery,
            });

            Assert.Equal(friendlyFire, received.FriendlyFire);
            Assert.Equal(recovery, received.RecoveryHealthPercent);
        }

        /// <summary>
        /// The two messages that state the session's rules have to state them identically. One is read at
        /// the door and the other whenever they change, by different code on different machines, so a
        /// field carried by one and dropped by the other is a session where joining and staying disagree.
        /// </summary>
        [Fact]
        public void HandshakeResult_CarriesTheSameRulesAsARuleChange()
        {
            HandshakeResultMessage received = RoundTrip(new HandshakeResultMessage
            {
                Accepted = true,
                FriendlyFire = true,
                RecoveryHealthPercent = 5,
            });

            Assert.True(received.FriendlyFire);
            Assert.Equal(5, received.RecoveryHealthPercent);
        }

        /// <summary>
        /// Same reasoning as the handshake's default: an unset rule has to read as off, because that
        /// failure is silent until somebody is killed by a friend.
        /// </summary>
        [Fact]
        public void SessionRules_DefaultsFriendlyFireOff()
        {
            Assert.False(default(SessionRulesMessage).FriendlyFire);
            Assert.False(RoundTrip(default(SessionRulesMessage)).FriendlyFire);
        }

        /// <summary>
        /// Exact equality rather than a tolerance on purpose: nothing in the path is allowed to round a
        /// coordinate, so a near-miss here is a serialization bug and not a floating point fact of life.
        /// </summary>
        [Fact]
        public void PlayerState_RoundTripsExactly()
        {
            PlayerStateMessage sent = new()
            {
                PlayerId = new PlayerId(2),
                X = -1234.5678f,
                Y = 0.1f,
                DirectionX = -0.7071f,
                DirectionY = 0.7071f,
                LookX = 1f,
                LookY = -0.0001f,
                MoveMode = 2,
                InteractionMode = 1,
                Action = 3,
                ActionSequence = 200,
                ActionReleased = 1,

                // Two adjacent fields of one type, so they are given different values: equal ones survive
                // a Read that returns them the wrong way round, and a health bar reading as a fraction of
                // the wrong maximum is a player who looks fine while dying.
                Health = 37,
                MaxHealth = 240,

                // Four ushorts in a row now, all distinct: swapping health with stamina would show up as
                // a guard that breaks at the wrong moment rather than as anything obviously broken.
                Stamina = 12,
                MaxStamina = 95,
                Downed = 1,
            };

            PlayerStateMessage received = RoundTrip(sent);

            Assert.Equal(sent.PlayerId, received.PlayerId);
            Assert.Equal(sent.X, received.X);
            Assert.Equal(sent.Y, received.Y);
            Assert.Equal(sent.DirectionX, received.DirectionX);
            Assert.Equal(sent.DirectionY, received.DirectionY);
            Assert.Equal(sent.LookX, received.LookX);
            Assert.Equal(sent.LookY, received.LookY);
            Assert.Equal(sent.MoveMode, received.MoveMode);
            Assert.Equal(sent.InteractionMode, received.InteractionMode);
            Assert.Equal(sent.Action, received.Action);
            Assert.Equal(sent.ActionSequence, received.ActionSequence);
            Assert.Equal(sent.ActionReleased, received.ActionReleased);
            Assert.Equal(sent.Health, received.Health);
            Assert.Equal(sent.MaxHealth, received.MaxHealth);
            Assert.Equal(sent.Stamina, received.Stamina);
            Assert.Equal(sent.MaxStamina, received.MaxStamina);
            Assert.Equal(sent.Downed, received.Downed);
        }

        /// <summary>
        /// On its feet is what a message that never said otherwise has to mean. The flag is the only thing
        /// keeping a body upright, so a default or truncated read that came out non-zero would drop a
        /// player who is fine to the floor and leave them there - every subsequent packet says the same.
        /// </summary>
        [Fact]
        public void PlayerState_DefaultsToStanding()
        {
            Assert.Equal(0, default(PlayerStateMessage).Downed);
            Assert.Equal(0, RoundTrip(default(PlayerStateMessage)).Downed);
        }

        /// <summary>
        /// An ordinary state has to survive the check, or the check is a way to stop coop working. The
        /// values here are deliberately unremarkable - the point is that nothing about a normal player
        /// looks implausible.
        /// </summary>
        [Fact]
        public void PlayerState_OrdinaryValuesArePlausible()
        {
            PlayerStateMessage state = new()
            {
                X = 1234.5f,
                Y = -987.25f,
                DirectionX = -1f,
                DirectionY = 1f,
                LookX = 0.7071f,
                LookY = -0.7071f,
            };

            Assert.True(state.IsPlausible());
            Assert.True(default(PlayerStateMessage).IsPlausible());
        }

        /// <summary>
        /// NaN is the value that matters. It reaches the body's transform through the movement module and
        /// then feeds the next frame's interpolation as its own starting point, so a single one leaves the
        /// body broken for the rest of the session - it cannot recover, because what would repair it is
        /// consuming its own poisoned output.
        /// </summary>
        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void PlayerState_RefusesNonFiniteCoordinates(float bad)
        {
            Assert.False(new PlayerStateMessage { X = bad }.IsPlausible());
            Assert.False(new PlayerStateMessage { Y = bad }.IsPlausible());
            Assert.False(new PlayerStateMessage { DirectionX = bad }.IsPlausible());
            Assert.False(new PlayerStateMessage { DirectionY = bad }.IsPlausible());
            Assert.False(new PlayerStateMessage { LookX = bad }.IsPlausible());
            Assert.False(new PlayerStateMessage { LookY = bad }.IsPlausible());
        }

        /// <summary>
        /// Finite and still nonsense. A coordinate this large is not somewhere a player can be followed
        /// to, and it is the other half of what the non-finite check misses.
        /// </summary>
        [Fact]
        public void PlayerState_RefusesAbsurdMagnitudes()
        {
            Assert.False(new PlayerStateMessage { X = PlayerStateMessage.MaxCoordinate * 2f }.IsPlausible());
            Assert.False(new PlayerStateMessage { Y = -PlayerStateMessage.MaxCoordinate * 2f }.IsPlausible());
            Assert.False(new PlayerStateMessage { DirectionX = PlayerStateMessage.MaxAxis * 2f }.IsPlausible());
            Assert.False(new PlayerStateMessage { LookY = -PlayerStateMessage.MaxAxis * 2f }.IsPlausible());

            // The limits themselves are accepted; a bound that refuses its own edge is a bound nobody can
            // reason about.
            Assert.True(new PlayerStateMessage { X = PlayerStateMessage.MaxCoordinate }.IsPlausible());
            Assert.True(new PlayerStateMessage { LookX = -PlayerStateMessage.MaxAxis }.IsPlausible());
        }

        /// <summary>
        /// The check has to survive the wire, not just the struct. A NaN written and read back is still a
        /// NaN, and this is the path it would actually arrive by.
        /// </summary>
        [Fact]
        public void PlayerState_NonFiniteSurvivesRoundTripAndIsStillRefused()
        {
            PlayerStateMessage received = RoundTrip(new PlayerStateMessage { X = float.NaN, Y = 5f });

            Assert.True(float.IsNaN(received.X));
            Assert.False(received.IsPlausible());
        }

        /// <summary>
        /// The build and the voice are asserted with values that differ from each other, and from the
        /// player id, on purpose. All three are bytes written next to each other, so a Read that returns
        /// them in the wrong order round-trips perfectly as long as the test sends the same number twice -
        /// and a body that comes out with somebody else's build is the exact bug this message exists to
        /// prevent.
        /// </summary>
        /// <summary>
        /// The ordinary case and the one the counter exists for. A repeat is the packet already acted on,
        /// and a step of one across the wrap is still a step of one - a comparison written as
        /// <c>arrived &gt; applied</c> passes every row above and fails that one, leaving a body that
        /// stops attacking every 256 swings for no reason anybody could reproduce on purpose.
        /// </summary>
        [Theory]
        [InlineData(1, 0, true)]
        [InlineData(0, 0, false)]
        [InlineData(200, 200, false)]
        [InlineData(0, 255, true)]
        [InlineData(3, 254, true)]
        public void ActionSequence_TreatsForwardStepsAsNewAcrossTheWrap(byte arrived, byte applied, bool expected)
        {
            Assert.Equal(expected, PlayerStateMessage.IsNewerAction(arrived, applied));
        }

        /// <summary>
        /// A packet overtaken by a newer one, which unreliable delivery produces routinely. Acting on it
        /// restarts an action its owner has already finished, and on screen that is the far player
        /// attacking twice - so the far half of the range has to read as the past, wrap included.
        /// </summary>
        [Theory]
        [InlineData(0, 1, false)]
        [InlineData(254, 3, false)]
        [InlineData(255, 0, false)]
        public void ActionSequence_TreatsLateArrivalsAsAlreadyApplied(byte arrived, byte applied, bool expected)
        {
            Assert.Equal(expected, PlayerStateMessage.IsNewerAction(arrived, applied));
        }

        /// <summary>
        /// The exact seam between the two halves, which no other test pins: at 127 ahead the packet is
        /// still the future, and one further is the past. Nothing in a session is expected to arrive this
        /// far out of order - the point is that the boundary cannot move unnoticed, because either side of
        /// it is a defensible rule and drifting between them silently is not.
        /// </summary>
        [Fact]
        public void ActionSequence_SplitsTheRangeInHalf()
        {
            Assert.True(PlayerStateMessage.IsNewerAction(127, 0));
            Assert.False(PlayerStateMessage.IsNewerAction(128, 0));
        }

        [Fact]
        public void Equipment_RoundTripsEveryItem()
        {
            PlayerEquipmentMessage sent = new()
            {
                PlayerId = new PlayerId(1),
                BodyType = 2,
                VoiceId = 3,
                ItemIds = ["Weapon_Spear", "Armor_Chest_Leather", "Amulet_Bone"],
            };

            PlayerEquipmentMessage received = RoundTrip(sent);

            Assert.Equal(sent.PlayerId, received.PlayerId);
            Assert.Equal(sent.BodyType, received.BodyType);
            Assert.Equal(sent.VoiceId, received.VoiceId);
            Assert.Equal(sent.ItemIds, received.ItemIds);
        }

        /// <summary>
        /// The cap itself is a legal set, not an overlong one. A bound written as the wrong comparison
        /// drops the last item of a full set, which is a weapon that silently does not arrive rather than
        /// anything that looks like a bug.
        /// </summary>
        [Fact]
        public void Equipment_KeepsExactlyTheMaximumIntact()
        {
            string[] full = Enumerable.Range(0, PlayerEquipmentMessage.MaxItems)
                .Select(index => $"Item_{index}")
                .ToArray();

            PlayerEquipmentMessage received = RoundTrip(new PlayerEquipmentMessage { ItemIds = full });

            Assert.Equal(full, received.ItemIds);
        }

        /// <summary>
        /// The sender's own caps, which is the case a bug in this mod produces rather than one an attacker
        /// does: more items than the message allows and an id longer than it allows, both dropped to the
        /// bound instead of writing a message the far side reads as garbage.
        /// </summary>
        [Fact]
        public void Equipment_BoundsCountAndIdLengthOnWrite()
        {
            string[] tooMany = Enumerable.Range(0, PlayerEquipmentMessage.MaxItems + 9)
                .Select(_ => new string('x', PlayerEquipmentMessage.MaxItemIdLength + 50))
                .ToArray();

            PlayerEquipmentMessage received = RoundTrip(new PlayerEquipmentMessage { ItemIds = tooMany });

            Assert.Equal(PlayerEquipmentMessage.MaxItems, received.ItemIds.Length);
            Assert.All(received.ItemIds, id => Assert.Equal(PlayerEquipmentMessage.MaxItemIdLength, id.Length));
        }

        /// <summary>
        /// An empty set is the ordinary state of a player who has just started, and it has to survive the
        /// trip as an empty array rather than as null - the receiver undresses a body from this list, and
        /// a null one would read as "no equipment message" instead of "no equipment".
        /// </summary>
        [Fact]
        public void Equipment_RoundTripsEmptyAsEmptyRatherThanNull()
        {
            PlayerEquipmentMessage received = RoundTrip(new PlayerEquipmentMessage { ItemIds = null! });

            Assert.NotNull(received.ItemIds);
            Assert.Empty(received.ItemIds);
        }

        /// <summary>
        /// The case an attacker controls: a sender that ignores both caps and writes the field by hand.
        /// The count bounds what the receiver allocates and the length bounds what reaches the log.
        ///
        /// Every field is written here, in the order <see cref="PlayerEquipmentMessage.Write"/> uses,
        /// because a hand-written stream that omits one does not fail - it slides the count into a
        /// neighboring byte and goes on reading strings out of the middle of other strings, which for a
        /// payload of one repeated character still satisfies every assertion below while testing nothing.
        /// </summary>
        [Fact]
        public void Equipment_BoundsCountAndIdLengthOnRead()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)1);
            writer.Put((byte)0);
            writer.Put((byte)0);
            writer.Put((byte)200);

            for (int index = 0; index < 200; index++)
            {
                writer.Put(new string('x', PlayerEquipmentMessage.MaxItemIdLength + 50));
            }

            stream.Position = 0;
            PlayerEquipmentMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(PlayerEquipmentMessage.MaxItems, received.ItemIds.Length);
            Assert.All(received.ItemIds, id => Assert.Equal(PlayerEquipmentMessage.MaxItemIdLength, id.Length));
        }

        /// <summary>
        /// The one rule <see cref="MessageIds"/> exists to enforce, checked by reflection so it keeps
        /// holding as ids are added rather than only for the three that exist today.
        /// </summary>
        [Fact]
        public void MessageIds_AreDistinct()
        {
            FieldInfo[] fields = typeof(MessageIds).GetFields(BindingFlags.Public | BindingFlags.Static);
            ushort[] ids = fields.Select(field => (ushort)field.GetRawConstantValue()!).ToArray();

            Assert.NotEmpty(ids);
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }

        private static T RoundTrip<T>(in T message) where T : struct, INetMessage
        {
            using MemoryStream stream = new();
            message.Write(new BufferWriter(stream));

            stream.Position = 0;
            T received = default;
            received.Read(new BufferReader(stream));

            return received;
        }
    }
}
