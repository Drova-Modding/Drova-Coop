using Xunit;
using static DrovaCoopMod.Messages.Tests.Wire;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// The pile message, and the rule that keeps shared hauling from driving a quest counter below what the game alone would reach.
    /// </summary>
    public class HaulPileTests
    {
        private const string Key = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        private const string Other = "11111111-2222-3333-4444-555555555555";

        [Fact]
        public void HaulPile_RoundTripsEveryField()
        {
            HaulPileMessage sent = new()
            {
                SpotKey = Key,
                Slot = 2,
                Kind = HaulPileMessage.KindDumped,
                ListGuid = "ffffffff-0000-1111-2222-333333333333",
                VarId = "OpenRubbleAmount",
                Player = 1,
                Sequence = 4711,
            };

            HaulPileMessage read = RoundTrip(sent);

            Assert.Equal(sent.SpotKey, read.SpotKey);
            Assert.Equal(sent.Slot, read.Slot);
            Assert.Equal(sent.Kind, read.Kind);
            Assert.Equal(sent.ListGuid, read.ListGuid);
            Assert.Equal(sent.VarId, read.VarId);
            Assert.Equal(sent.Player, read.Player);
            Assert.Equal(sent.Sequence, read.Sequence);
        }

        [Fact]
        public void HaulPile_RoundTripsAPickupWithNoVariable()
        {
            HaulPileMessage read = RoundTrip(new HaulPileMessage
            {
                SpotKey = Key,
                Slot = 0,
                Kind = HaulPileMessage.KindTaken,
                ListGuid = string.Empty,
                VarId = string.Empty,
            });

            Assert.Equal(HaulPileMessage.KindTaken, read.Kind);
            Assert.Equal(string.Empty, read.ListGuid);
            Assert.Equal(string.Empty, read.VarId);
            Assert.True(read.IsWellFormed());
        }

        [Fact]
        public void HaulPile_BoundsEveryStringFromAStranger()
        {
            HaulPileMessage read = RoundTrip(new HaulPileMessage
            {
                SpotKey = new string('a', 500),
                Slot = 1,
                Kind = HaulPileMessage.KindDumped,
                ListGuid = new string('b', 500),
                VarId = new string('c', 500),
            });

            Assert.Equal(ResourceWorkMessage.MaxKeyLength, read.SpotKey.Length);
            Assert.Equal(GVarChangeMessage.MaxGuidLength, read.ListGuid.Length);
            Assert.Equal(GVarChangeMessage.MaxIdLength, read.VarId.Length);
        }

        [Theory]

        // Runtime-created roots number themselves per savegame, so those ids alias between machines.
        [InlineData("0")]
        [InlineData("2")]
        [InlineData("Player#0")]
        [InlineData("")]
        [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeee")]
        public void HaulPile_RefusesAKeyThatCouldNameSomethingElse(string key)
        {
            Assert.False(HaulPileMessage.IsReplicablePile(key, 1));
        }

        [Fact]
        public void HaulPile_RefusesTheUnknownSlot()
        {
            Assert.True(HaulPileMessage.IsReplicablePile(Key, 0));
            Assert.False(HaulPileMessage.IsReplicablePile(Key, HaulPileMessage.SlotUnknown));
        }

        [Theory]
        [InlineData((byte)0)]
        [InlineData((byte)7)]
        public void HaulPile_RefusesAnUnknownKind(byte kind)
        {
            HaulPileMessage message = new() { SpotKey = Key, Slot = 1, Kind = kind };

            Assert.False(message.IsWellFormed());
        }

        [Fact]
        public void HaulPile_RefusesADumpThatNamesNoVariable()
        {
            HaulPileMessage message = new()
            {
                SpotKey = Key,
                Slot = 1,
                Kind = HaulPileMessage.KindDumped,
                ListGuid = "ffffffff-0000-1111-2222-333333333333",
                VarId = string.Empty,
            };

            Assert.False(message.IsWellFormed());
        }

        [Fact]
        public void Ledger_SubtractsOncePerPile()
        {
            HaulLedger ledger = new();
            string id = HaulPileMessage.MakeId(Key, 2);

            ledger.Tag(id);

            Assert.Equal(HaulLedger.Decision.Sub, ledger.DecideSub(id));

            ledger.RecordSub(id);

            Assert.Equal(HaulLedger.Decision.DedupSub, ledger.DecideSub(id));
            Assert.Equal(HaulLedger.Decision.DedupSub, ledger.DecideSub(id));
        }

        /// <summary>A world only pays for a pile it produced, and each produced pile came with exactly one authored addition.</summary>
        [Fact]
        public void Ledger_RefusesAPileThisWorldNeverHad()
        {
            HaulLedger ledger = new();
            string mine = HaulPileMessage.MakeId(Key, 2);
            string theirs = HaulPileMessage.MakeId(Other, 2);

            ledger.Tag(mine);

            Assert.Equal(HaulLedger.Decision.RefuseUntagged, ledger.DecideSub(theirs));
            Assert.Equal(HaulLedger.Decision.RefuseUntagged, ledger.DecideRemoval(theirs));

            ledger.RecordSub(theirs);

            // A recorded payment does not grant permission. The refusal is about production.
            Assert.Equal(HaulLedger.Decision.RefuseUntagged, ledger.DecideSub(theirs));
            Assert.Equal(0, ledger.Tagged - 1);
        }

        [Fact]
        public void Ledger_DumpedImpliesTaken()
        {
            HaulLedger ledger = new();
            string id = HaulPileMessage.MakeId(Key, 3);

            ledger.Tag(id);

            // The dump arrived first. It has to do the removal as well.
            Assert.Equal(HaulLedger.Decision.Remove, ledger.DecideRemoval(id));
            ledger.RecordRemoval(id);

            Assert.Equal(HaulLedger.Decision.Sub, ledger.DecideSub(id));
            ledger.RecordSub(id);

            // The pickup message then turns up late and must do nothing at all.
            Assert.Equal(HaulLedger.Decision.AlreadyRemoved, ledger.DecideRemoval(id));
            Assert.Equal(HaulLedger.Decision.DedupSub, ledger.DecideSub(id));
        }

        [Fact]
        public void Ledger_LocalAndRemoteDumpAgree()
        {
            string id = HaulPileMessage.MakeId(Key, 1);

            // This world dumped first. The teammate's copy of the same pile arrives afterwards.
            HaulLedger first = new();
            first.Tag(id);
            first.RecordSub(id);

            Assert.Equal(HaulLedger.Decision.DedupSub, first.DecideSub(id));

            // The mirror case: the teammate's dump landed first, so the local one must be skipped.
            HaulLedger second = new();
            second.Tag(id);

            Assert.Equal(HaulLedger.Decision.Sub, second.DecideSub(id));
            second.RecordSub(id);

            Assert.True(second.HasSubbed(id));
        }

        /// <summary>Every interleaving of three piles' pickups and dumps lands exactly three subtractions.</summary>
        [Fact]
        public void Ledger_IsOrderIndependent()
        {
            string[] ids =
            [
                HaulPileMessage.MakeId(Key, 0),
                HaulPileMessage.MakeId(Key, 1),
                HaulPileMessage.MakeId(Other, 4),
            ];

            (string Id, byte Kind)[] events =
            [
                (ids[0], HaulPileMessage.KindTaken),
                (ids[0], HaulPileMessage.KindDumped),
                (ids[1], HaulPileMessage.KindTaken),
                (ids[1], HaulPileMessage.KindDumped),
                (ids[2], HaulPileMessage.KindTaken),
                (ids[2], HaulPileMessage.KindDumped),
            ];

            foreach (int[] order in Permutations(events.Length))
            {
                HaulLedger ledger = new();

                foreach (string id in ids)
                {
                    ledger.Tag(id);
                }

                int subs = 0;

                foreach (int index in order)
                {
                    (string id, byte kind) = events[index];

                    if (ledger.DecideRemoval(id) == HaulLedger.Decision.Remove) ledger.RecordRemoval(id);

                    if (kind != HaulPileMessage.KindDumped) continue;

                    if (ledger.DecideSub(id) != HaulLedger.Decision.Sub) continue;

                    ledger.RecordSub(id);
                    subs++;
                }

                Assert.Equal(3, subs);
                Assert.Equal(3, ledger.Subbed);
            }
        }

        [Fact]
        public void Ledger_ReportsPilesATeammateHasNotDumped()
        {
            HaulLedger ledger = new();
            string carried = HaulPileMessage.MakeId(Key, 1);
            string dumped = HaulPileMessage.MakeId(Key, 0);

            ledger.Tag(carried);
            ledger.Tag(dumped);

            ledger.RecordTakenByPeer(1, carried);
            ledger.RecordTakenByPeer(1, dumped);

            Assert.Equal(2, ledger.OrphanCount());

            ledger.RecordSub(dumped);
            ledger.ClearTakenByPeer(dumped);

            Assert.Equal(1, ledger.OrphanCount());
            Assert.Equal(carried, Assert.Single(ledger.Orphans()));
            Assert.Empty(ledger.Orphans(2));
        }

        /// <summary>Every ordering of n items.</summary>
        private static IEnumerable<int[]> Permutations(int count)
        {
            int[] order = new int[count];

            for (int index = 0; index < count; index++)
            {
                order[index] = index;
            }

            return Permute(order, 0);
        }

        private static IEnumerable<int[]> Permute(int[] order, int from)
        {
            if (from == order.Length - 1)
            {
                yield return (int[])order.Clone();
                yield break;
            }

            for (int index = from; index < order.Length; index++)
            {
                (order[from], order[index]) = (order[index], order[from]);

                foreach (int[] tail in Permute(order, from + 1))
                {
                    yield return tail;
                }

                (order[from], order[index]) = (order[index], order[from]);
            }
        }
    }
}
