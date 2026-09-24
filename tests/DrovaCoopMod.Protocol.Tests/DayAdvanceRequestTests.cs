using System.IO;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Messages.Tests;
using Xunit;
using static DrovaCoopMod.Messages.Tests.Wire;

namespace DrovaCoopMod.Messages.DayAdvanceTests
{
    /// <summary>
    /// The ask a client sends when its player sleeps, and the plausibility test the host runs on it
    /// before anything touches a clock.
    ///
    /// Worth testing here because a day cannot be un-advanced. The host's other guards - forward only,
    /// night-sized, no further than a day, not twice in a row - are about its own clock and belong with
    /// it; this is only about what may cross the wire at all.
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
            };

            DayAdvanceRequestMessage read = RoundTrip(sent);

            Assert.Equal(sent.Player.Value, read.Player.Value);
            Assert.Equal(sent.TargetTotalSeconds, read.TargetTotalSeconds);
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
