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
