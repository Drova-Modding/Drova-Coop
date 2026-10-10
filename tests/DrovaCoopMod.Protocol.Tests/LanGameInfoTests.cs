using Xunit;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// The LAN discovery answer and the direct address field. Both read text from strangers.
    /// </summary>
    public class LanGameInfoTests
    {
        [Fact]
        public void Encode_RoundTrips()
        {
            LanGameInfo sent = new(CoopProtocol.Version, 3, 16, "Hasso");

            Assert.True(LanGameInfo.TryDecode(sent.Encode(), out LanGameInfo read));
            Assert.Equal(sent, read);
        }

        [Fact]
        public void Decode_RefusesForeignAndTruncated()
        {
            byte[] good = new LanGameInfo(CoopProtocol.Version, 1, 16, "A").Encode();

            Assert.False(LanGameInfo.TryDecode([], out _));
            Assert.False(LanGameInfo.TryDecode("XXXX123456"u8.ToArray(), out _));
            Assert.False(LanGameInfo.TryDecode(good[..^2], out _));
        }

        [Fact]
        public void Decode_CutsLongNames()
        {
            byte[] data = new LanGameInfo(CoopProtocol.Version, 1, 16, new string('x', 300)).Encode();

            Assert.True(LanGameInfo.TryDecode(data, out LanGameInfo read));
            Assert.Equal(HandshakeMessage.MaxPlayerNameLength, read.HostName.Length);
        }

        [Fact]
        public void Joinable_NeedsSameProtocolAndRoom()
        {
            Assert.True(new LanGameInfo(76, 1, 16, "A").IsJoinable(76));
            Assert.False(new LanGameInfo(75, 1, 16, "A").IsJoinable(76));
            Assert.False(new LanGameInfo(76, 16, 16, "A").IsJoinable(76));
        }

        [Theory]
        [InlineData("192.168.1.5", "192.168.1.5", LanGameInfo.DefaultPort)]
        [InlineData(" 192.168.1.5:7000 ", "192.168.1.5", 7000)]
        [InlineData("example.org:9061", "example.org", 9061)]
        [InlineData("[fe80::1]:7000", "fe80::1", 7000)]
        [InlineData("[fe80::1]", "fe80::1", LanGameInfo.DefaultPort)]
        [InlineData("fe80::1", "fe80::1", LanGameInfo.DefaultPort)]
        public void ParseAddress_Accepts(string text, string host, int port)
        {
            Assert.True(LanGameInfo.TryParseAddress(text, LanGameInfo.DefaultPort, out string parsedHost, out int parsedPort));
            Assert.Equal(host, parsedHost);
            Assert.Equal(port, parsedPort);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(":9061")]
        [InlineData("host:0")]
        [InlineData("host:70000")]
        [InlineData("host:abc")]
        [InlineData("[fe80::1")]
        [InlineData("[fe80::1]x")]
        public void ParseAddress_Refuses(string? text)
        {
            Assert.False(LanGameInfo.TryParseAddress(text, LanGameInfo.DefaultPort, out _, out _));
        }
    }
}
