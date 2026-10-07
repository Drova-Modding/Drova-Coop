using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Messages.Tests;
using Xunit;
using static DrovaCoopMod.Messages.Tests.Wire;

namespace DrovaCoopMod.Messages.ResourceTests
{
    /// <summary>The wire contract for shared mining.</summary>
    public class ResourceWorkTests
    {
        [Fact]
        public void ResourceWork_RoundTripsEveryField()
        {
            ResourceWorkMessage sent = new()
            {
                RootKey = "2eb31181-1f4c-4f4e-9a1e-0d2b6f7c8a91",
                ChargesAfter = 2,
                ChargesBefore = 3,
                Flags = ResourceWorkMessage.FlagByPlayer,
                Player = 1,
                Sequence = 4211,
            };

            ResourceWorkMessage received = RoundTrip(sent);

            Assert.Equal("2eb31181-1f4c-4f4e-9a1e-0d2b6f7c8a91", received.RootKey);
            Assert.Equal(2, received.ChargesAfter);
            Assert.Equal(3, received.ChargesBefore);
            Assert.Equal(ResourceWorkMessage.FlagByPlayer, received.Flags);
            Assert.Equal(1, received.Player);
            Assert.Equal(4211u, received.Sequence);
        }

        [Fact]
        public void ResourceWork_BoundsAKeyFromAStranger()
        {
            ResourceWorkMessage received = RoundTrip(new ResourceWorkMessage
            {
                RootKey = new string('a', ResourceWorkMessage.MaxKeyLength + 500),
            });

            Assert.Equal(ResourceWorkMessage.MaxKeyLength, received.RootKey.Length);
        }

        [Fact]
        public void SpotSnapshot_RoundTripsItsRecords()
        {
            ResourceSpotSnapshotMessage sent = new()
            {
                Records =
                [
                    new ResourceSpotSnapshotMessage.Record
                    {
                        RootKey = "2eb31181-1f4c-4f4e-9a1e-0d2b6f7c8a91",
                        Charges = 0,
                    },
                    new ResourceSpotSnapshotMessage.Record
                    {
                        RootKey = "9f0c2d43-5b6a-4c7d-8e9f-0a1b2c3d4e5f",
                        Charges = 5,
                    },
                ],
            };

            ResourceSpotSnapshotMessage received = RoundTrip(sent);

            Assert.Equal(2, received.Records.Length);
            Assert.Equal("2eb31181-1f4c-4f4e-9a1e-0d2b6f7c8a91", received.Records[0].RootKey);
            Assert.Equal(0, received.Records[0].Charges);
            Assert.Equal(5, received.Records[1].Charges);
        }

        [Fact]
        public void SpotSnapshot_BoundsTheRecordCountItAllocates()
        {
            // The sender does not get to choose how much memory the receiver allocates.
            ResourceSpotSnapshotMessage sent = new()
            {
                Records = new ResourceSpotSnapshotMessage.Record[ResourceSpotSnapshotMessage.MaxRecords + 40],
            };

            for (int index = 0; index < sent.Records.Length; index++)
            {
                sent.Records[index] = new ResourceSpotSnapshotMessage.Record { RootKey = "k", Charges = 1 };
            }

            ResourceSpotSnapshotMessage received = RoundTrip(sent);

            Assert.Equal(ResourceSpotSnapshotMessage.MaxRecords, received.Records.Length);
        }

        [Fact]
        public void SpotSnapshotEnd_RoundTripsItsCount()
        {
            Assert.Equal(78u, RoundTrip(new ResourceSpotSnapshotEndMessage { RecordCount = 78 }).RecordCount);
        }

        [Theory]
        [InlineData("2eb31181-1f4c-4f4e-9a1e-0d2b6f7c8a91")]
        [InlineData("2EB31181-1F4C-4F4E-9A1E-0D2B6F7C8A91")]
        public void AGuidShapedKeyIsReplicable(string key)
        {
            Assert.True(ResourceWorkMessage.IsReplicableKey(key));
        }

        [Fact]
        public void AGuidWithACustomSuffixIsReplicable()
        {
            // A root may append a key of its own, giving the 72-character keys in real saves.
            string key = "2eb31181-1f4c-4f4e-9a1e-0d2b6f7c8a91" + "9f0c2d43-5b6a-4c7d-8e9f-0a1b2c3d4e5f";

            Assert.Equal(72, key.Length);
            Assert.True(ResourceWorkMessage.IsReplicableKey(key));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("Player#")]
        [InlineData("Player#0")]
        public void AKeyThatNamesNothingSharableIsRefused(string? key)
        {
            Assert.False(ResourceWorkMessage.IsReplicableKey(key!));
        }

        // Runtime objects number their save roots per savegame, so the same number names a different object elsewhere.
        [Theory]
        [InlineData("0")]
        [InlineData("1")]
        [InlineData("2")]
        [InlineData("17")]
        public void APerSavegameCounterIsRefusedBecauseItAliases(string key)
        {
            Assert.False(ResourceWorkMessage.IsReplicableKey(key));
        }

        [Theory]
        [InlineData("2eb31181-1f4c-4f4e-9a1e-0d2b6f7c8a9")]   // one short
        [InlineData("2eb31181-1f4c-4f4e-9a1e-0d2b6f7c8a911")] // one long
        [InlineData("2eb31181-1f4c-4f4e-9a1e-0d2b6f7c8zzz")]  // right length, not hex
        public void AKeyOfTheWrongShapeIsRefused(string key)
        {
            Assert.False(ResourceWorkMessage.IsReplicableKey(key));
        }
    }
}
