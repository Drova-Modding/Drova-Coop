using Xunit;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// Position keys, which name resource spots the save system never named.
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
            // A bare number names a different object in every savegame.
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
