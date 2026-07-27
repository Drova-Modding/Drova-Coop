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
