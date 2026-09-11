using DrovaCoopMod.Messages;
using Xunit;

namespace DrovaCoopMod.Messages.PositionKeyTests
{
    /// <summary>
    /// The second way a resource spot can be named, for spots the save system never named.
    ///
    /// Worth testing hard despite being three lines: a key that two machines compute differently is a
    /// spot that never matches, and a key shape that lets something else through is the aliasing
    /// failure the whole rule exists to prevent.
    /// </summary>
    public class PositionKeyTests
    {
        [Fact]
        public void APositionKeyIsReplicable()
        {
            string key = ResourceWorkMessage.MakePositionKey(23371, 59787);

            Assert.True(ResourceWorkMessage.IsPositionKey(key));
            Assert.True(ResourceWorkMessage.IsReplicableKey(key));
        }

        [Fact]
        public void NegativeCoordinatesSurvive()
        {
            string key = ResourceWorkMessage.MakePositionKey(-760, -7722);

            Assert.Equal("@-760,-7722", key);
            Assert.True(ResourceWorkMessage.IsReplicableKey(key));
        }

        [Fact]
        public void TwoSpotsOneUnitApartAreDifferentKeys()
        {
            Assert.NotEqual(
                ResourceWorkMessage.MakePositionKey(100, 200),
                ResourceWorkMessage.MakePositionKey(101, 200));
        }

        [Fact]
        public void TheSamePlaceAlwaysMakesTheSameKey()
        {
            Assert.Equal(
                ResourceWorkMessage.MakePositionKey(23371, 59787),
                ResourceWorkMessage.MakePositionKey(23371, 59787));
        }

        [Theory]
        [InlineData("@")]
        [InlineData("@,")]
        [InlineData("@1")]
        [InlineData("@1,")]
        [InlineData("@,1")]
        [InlineData("@a,1")]
        [InlineData("@1,b")]
        [InlineData("@1 2,3")]
        [InlineData("@-,1")]
        [InlineData("@12345678901,1")]
        [InlineData("@1,2,3")]
        public void MalformedPositionKeysAreRefused(string key)
        {
            Assert.False(ResourceWorkMessage.IsPositionKey(key));
            Assert.False(ResourceWorkMessage.IsReplicableKey(key));
        }

        [Fact]
        public void ADynamicRootsCounterIsStillRefused()
        {
            // The failure the position key exists alongside, not in place of. A bare number names a
            // different object in every savegame, and no prefix makes that safe.
            Assert.False(ResourceWorkMessage.IsReplicableKey("2"));
            Assert.False(ResourceWorkMessage.IsReplicableKey("@2"));
        }

        [Fact]
        public void AGuidKeyIsStillReplicableAndIsNotAPositionKey()
        {
            const string guid = "d10069ed-1234-4321-abcd-0123456789ab";

            Assert.True(ResourceWorkMessage.IsReplicableKey(guid));
            Assert.False(ResourceWorkMessage.IsPositionKey(guid));
        }

        [Fact]
        public void APileAtAPositionNamedSpotIsReplicable()
        {
            string key = ResourceWorkMessage.MakePositionKey(23371, 59787);

            Assert.True(HaulPileMessage.IsReplicablePile(key, 2));
            Assert.False(HaulPileMessage.IsReplicablePile(key, HaulPileMessage.SlotUnknown));
        }
    }
}
