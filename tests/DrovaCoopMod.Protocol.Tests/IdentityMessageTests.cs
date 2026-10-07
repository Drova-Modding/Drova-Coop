using Drova_Modding_API.Systems.Networking;
using Xunit;
using static DrovaCoopMod.Messages.Tests.Wire;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// Player identity messages: ids survive the wire and a hostile roster cannot size allocations.
    /// </summary>
    public class IdentityMessageTests
    {
        [Fact]
        public void UnassignedIsNotTheHost()
        {
            Assert.NotEqual(PlayerId.Host, PlayerId.None);
            Assert.True(PlayerId.Host.IsHost);
            Assert.True(PlayerId.Host.IsAssigned);
            Assert.False(PlayerId.None.IsAssigned);
            Assert.False(PlayerId.None.IsHost);
        }

        [Fact]
        public void PlayerJoinedRoundTrips()
        {
            PlayerJoinedMessage sent = new() { Id = new PlayerId(2), Name = "Nemeton" };

            PlayerJoinedMessage received = RoundTrip(sent);

            Assert.Equal(new PlayerId(2), received.Id);
            Assert.Equal("Nemeton", received.Name);
        }

        [Fact]
        public void PlayerJoinedTruncatesAnOverlongName()
        {
            PlayerJoinedMessage sent = new()
            {
                Id = new PlayerId(1),
                Name = new string('n', HandshakeMessage.MaxPlayerNameLength + 100),
            };

            PlayerJoinedMessage received = RoundTrip(sent);

            Assert.Equal(HandshakeMessage.MaxPlayerNameLength, received.Name.Length);
        }

        // A reply must stay distinct from a fresh ping, or two peers answer each other forever.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PingRoundTrips(bool isReply)
        {
            PingMessage sent = new()
            {
                Sender = new PlayerId(2),
                Token = 4294967295,
                IsReply = isReply,
            };

            PingMessage received = RoundTrip(sent);

            Assert.Equal(new PlayerId(2), received.Sender);
            Assert.Equal(4294967295, received.Token);
            Assert.Equal(isReply, received.IsReply);
        }

        [Fact]
        public void PlayerLeftRoundTrips()
        {
            PlayerLeftMessage received = RoundTrip(new PlayerLeftMessage { Id = new PlayerId(3) });

            Assert.Equal(new PlayerId(3), received.Id);
        }

        [Fact]
        public void RosterRoundTripsEveryEntryInOrder()
        {
            RosterSnapshotMessage sent = new()
            {
                Players =
                [
                    new RosterEntry(PlayerId.Host, "Host"),
                    new RosterEntry(new PlayerId(1), "Nemeton"),
                    new RosterEntry(new PlayerId(2), "Mara"),
                ],
            };

            RosterSnapshotMessage received = RoundTrip(sent);

            Assert.Equal(3, received.Players.Length);
            Assert.Equal(PlayerId.Host, received.Players[0].Id);
            Assert.Equal("Nemeton", received.Players[1].Name);
            Assert.Equal(new PlayerId(2), received.Players[2].Id);
        }

        [Fact]
        public void RosterWritesNullPlayersAsEmpty()
        {
            RosterSnapshotMessage received = RoundTrip(new RosterSnapshotMessage { Players = null! });

            Assert.Empty(received.Players);
        }

        // The bound must hold on read, since the count comes from the sender.
        [Fact]
        public void RosterCapsAHostileCount()
        {
            using MemoryStream stream = new();
            BufferWriter writer = new(stream);
            writer.Put((byte)255);

            for (int index = 0; index < 255; index++)
            {
                writer.Put((byte)index);
                writer.Put("player");
            }

            stream.Position = 0;
            RosterSnapshotMessage received = default;
            received.Read(new BufferReader(stream));

            Assert.Equal(RosterSnapshotMessage.MaxPlayers, received.Players.Length);
        }
    }
}
