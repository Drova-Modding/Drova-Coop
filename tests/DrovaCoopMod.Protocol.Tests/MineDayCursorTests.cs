using DrovaCoopMod.Sync;
using Xunit;

namespace DrovaCoopMod.Messages.MineTests
{
    /// <summary>
    /// Where the mine's bed belongs in its list of days. The rule is the game's own; what is tested here
    /// is that it agrees with the game's and that it never rewinds.
    /// </summary>
    public class MineDayCursorTests
    {
        [Fact]
        public void NothingBegun_StaysAtTheFirstDay()
        {
            Assert.Equal(0, MineDayCursor.Resolve([false, false, false], 0));
        }

        /// <summary>
        /// The case that stranded a live session: one machine ran the rollover, the flag crossed, and
        /// the other machine's bed was still pointing at the day that had just been lived.
        /// </summary>
        [Fact]
        public void SecondDayBegun_MovesPastIt()
        {
            Assert.Equal(2, MineDayCursor.Resolve([false, true, false], 0));
        }

        /// <summary>
        /// The window between a player starting a morning and that scene writing its flag. Rewinding
        /// here would let the same morning be started twice.
        /// </summary>
        [Fact]
        public void NeverRewinds_WhileTheSceneIsStillWriting()
        {
            Assert.Equal(1, MineDayCursor.Resolve([false, false, false], 1));
        }

        /// <summary>Matches the authored loop, which breaks on the first match rather than the last.</summary>
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
