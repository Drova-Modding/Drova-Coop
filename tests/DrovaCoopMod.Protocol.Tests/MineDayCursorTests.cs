using DrovaCoopMod.Sync;
using Xunit;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>The mine bed's day cursor agrees with the game's rule and never rewinds.</summary>
    public class MineDayCursorTests
    {
        [Fact]
        public void NothingBegun_StaysAtTheFirstDay()
        {
            Assert.Equal(0, MineDayCursor.Resolve([false, false, false], 0));
        }

        /// <summary>The teammate ran the rollover and its flag crossed, so this bed must move on too.</summary>
        [Fact]
        public void SecondDayBegun_MovesPastIt()
        {
            Assert.Equal(2, MineDayCursor.Resolve([false, true, false], 0));
        }

        /// <summary>Before a started morning writes its flag, rewinding would let it start twice.</summary>
        [Fact]
        public void NeverRewinds_WhileTheSceneIsStillWriting()
        {
            Assert.Equal(1, MineDayCursor.Resolve([false, false, false], 1));
        }

        /// <summary>Matches the authored loop, which breaks on the first match.</summary>
        [Fact]
        public void FirstTrueWins_WhenTwoAreSet()
        {
            Assert.Equal(1, MineDayCursor.Resolve([true, true, false], 0));
        }

        [Fact]
        public void PastTheEnd_IsIdempotent()
        {
            Assert.Equal(3, MineDayCursor.Resolve([false, false, true], 3));
        }

        [Fact]
        public void NullOrEmpty_IsSafe()
        {
            Assert.Equal(2, MineDayCursor.Resolve(null, 2));
            Assert.Equal(2, MineDayCursor.Resolve([], 2));
        }
    }
}
