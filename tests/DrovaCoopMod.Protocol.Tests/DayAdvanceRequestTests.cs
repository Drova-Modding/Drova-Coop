using Xunit;
using static DrovaCoopMod.Messages.Tests.Wire;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// The client's sleep request and the host's plausibility test, since a day cannot be un-advanced.
    /// </summary>
    public class DayAdvanceRequestTests
    {
        [Fact]
        public void Request_RoundTripsEveryField()
        {
            DayAdvanceRequestMessage sent = new()
            {
                Player = new PlayerId(3),
                TargetTotalSeconds = 5 * 86400d + 7 * 3600d,
                Scripted = true,
            };

            DayAdvanceRequestMessage read = RoundTrip(sent);

            Assert.Equal(sent.Player.Value, read.Player.Value);
            Assert.Equal(sent.TargetTotalSeconds, read.TargetTotalSeconds);
            Assert.True(read.Scripted);
        }

        [Theory]
        [InlineData(0d)]
        [InlineData(3600d)]
        [InlineData(86400d)]
        [InlineData(365d * 86400d)]
        public void Plausible_AcceptsATimeAGameCouldBeAt(double seconds)
        {
            DayAdvanceRequestMessage message = new() { TargetTotalSeconds = seconds };

            Assert.True(message.IsPlausible());
        }

        [Theory]
        [InlineData(-1d)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(100_000d * 86400d)]
        public void Plausible_RefusesWhatNoClockCouldRead(double seconds)
        {
            DayAdvanceRequestMessage message = new() { TargetTotalSeconds = seconds };

            Assert.False(message.IsPlausible());
        }
    }
}
