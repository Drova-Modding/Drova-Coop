using Xunit;

namespace DrovaCoopMod.Messages.Tests
{
    public class SteamJoinTests
    {
        private const ulong Host = 76561198000000001;

        [Fact]
        public void JoinString_RoundTrips()
        {
            Assert.True(SteamJoin.TryParse(SteamJoin.Format(Host), out ulong id));
            Assert.Equal(Host, id);
        }

        // Steam appends the join string to whatever the game was started with.
        [Fact]
        public void CommandLine_FindsTheJoinAmongOtherArguments()
        {
            string[] arguments = { "Drova.exe", "--melonloader.debug", SteamJoin.Switch, Host.ToString(), "-screen-fullscreen" };

            Assert.True(SteamJoin.TryParse(arguments, out ulong id));
            Assert.Equal(Host, id);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("+drova_coop_join")]
        [InlineData("+drova_coop_join 0")]
        [InlineData("+drova_coop_join notanid")]
        [InlineData("+connect 76561198000000001")]
        public void Garbage_IsRefused(string? joinString)
        {
            Assert.False(SteamJoin.TryParse(joinString, out _));
        }
    }
}
