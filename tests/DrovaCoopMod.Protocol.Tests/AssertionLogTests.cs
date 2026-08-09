using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Messages.Tests;
using DrovaCoopMod.Sync;
using Xunit;

namespace DrovaCoopMod.Sync.Tests
{
    /// <summary>
    /// What the ordering log has to hold to.
    ///
    /// These are here rather than in a playthrough because that is the whole point: the rules below
    /// are decidable from numbers, and every one of them was previously only observable by playing a
    /// twenty-minute quest twice on two machines and diffing the savegames afterwards.
    /// </summary>
    public class AssertionLogTests
    {
        [Fact]
        public void ReleasesInStampOrderWhenMessagesArriveInverted()
        {
            // The shape the write hook actually produces. SetValue runs its listeners from inside
            // itself, so an inner write finishes - and is sent - before the outer one that caused it.
            // Note which way round that makes things: the LOWER stamp arrives LAST. A log that
            // released as soon as the run looked contiguous would ship every effect before its cause.
            AssertionLog log = new(settleSeconds: 0.05f);

            log.Offer(Assertion(sequence: 2, value: 1));
            log.Offer(Assertion(sequence: 3, value: 1));
            Assert.Empty(Drain(log));

            log.Offer(Assertion(sequence: 1, value: 1));

            log.Tick(0.06f);

            Assert.Equal(new uint[] { 1, 2, 3 }, Drain(log));
            Assert.Equal(1, log.Reordered);
        }

        [Fact]
        public void ReleasesNothingUntilTheWindowCloses()
        {
            AssertionLog log = new(settleSeconds: 0.05f);

            log.Offer(Assertion(sequence: 1, value: 1));

            log.Tick(0.02f);
            Assert.Empty(Drain(log));
            Assert.Equal(1, log.Pending);

            log.Tick(0.04f);
            Assert.Equal(new uint[] { 1 }, Drain(log));
        }

        [Fact]
        public void ReleasesWhatIsThereRatherThanWaitingForAStampThatNeverArrives()
        {
            AssertionLog log = new(settleSeconds: 0.05f);

            log.Offer(Assertion(sequence: 3, value: 1));
            log.Offer(Assertion(sequence: 4, value: 1));
            log.Offer(Assertion(sequence: 5, value: 1));

            log.Tick(0.06f);

            // Stamp 2 was lost on a reconnect. Waiting forever for it would be a quieter failure than
            // the one this class exists to end.
            Assert.Equal(new uint[] { 3, 4, 5 }, Drain(log));
        }

        [Fact]
        public void RefusesAStragglerFromOutsideItsWindow()
        {
            AssertionLog log = new(settleSeconds: 0.05f);

            log.Offer(Assertion(sequence: 1, value: 1));
            log.Offer(Assertion(sequence: 2, value: 0));
            log.Tick(0.06f);
            Assert.Equal(new uint[] { 1, 2 }, Drain(log));

            // A duplicate, or a cause so late that its effects have already been applied. Either way,
            // applying it now would undo the newer value with an older one.
            log.Offer(Assertion(sequence: 1, value: 1));
            log.Tick(0.06f);

            Assert.Empty(Drain(log));
            Assert.Equal(1, log.Skipped);
        }

        /// <summary>
        /// The 189-drop, from the live session of 2026-08-09.
        ///
        /// A deep cascade is sent innermost-first, so a long burst arrives with descending stamps. If
        /// the settling window closes partway through it - which it will, because the burst outlasts
        /// one window - everything still to come carries a lower stamp than what was just released.
        /// With one release floor per SENDER, all of it is discarded as stragglers: the host threw
        /// away 189 of the client's 363 assertions and said so only as a counter. The floor has to be
        /// per variable, because that is the only thing it is actually protecting.
        /// </summary>
        [Fact]
        public void DoesNotDiscardALateCauseJustBecauseAnotherVariableWasReleasedFirst()
        {
            AssertionLog log = new(settleSeconds: 0.05f);

            // First window: the tail of the cascade.
            log.Offer(Assertion(sequence: 300, value: 1));
            log.Offer(Assertion(sequence: 299, value: 1));
            log.Tick(0.06f);
            Assert.Equal(new uint[] { 299, 300 }, Drain(log));

            // Second window: the rest of the same cascade, all of it lower-stamped and all of it for
            // different variables. Not one of these may be lost.
            for (uint sequence = 298; sequence >= 290; sequence--)
            {
                log.Offer(Assertion(sequence, value: 1));
            }

            log.Tick(0.06f);

            Assert.Equal(new uint[] { 290, 291, 292, 293, 294, 295, 296, 297, 298 }, Drain(log));
            Assert.Equal(0, log.Skipped);
        }

        [Fact]
        public void StillRefusesAnOlderValueForTheSameVariable()
        {
            AssertionLog log = new(settleSeconds: 0.05f);

            GVarChangeMessage newer = Assertion(sequence: 10, value: 1);
            GVarChangeMessage older = Assertion(sequence: 4, value: 0);
            older.VarId = newer.VarId;

            log.Offer(newer);
            log.Tick(0.06f);
            Assert.Single(Drain(log));

            // Same variable, older stamp. Applying it would undo the newer value.
            log.Offer(older);
            log.Tick(0.06f);

            Assert.Empty(Drain(log));
            Assert.Equal(1, log.Skipped);
        }

        [Fact]
        public void KeepsTwoSendersApart()
        {
            AssertionLog log = new(settleSeconds: 0.05f);

            log.Offer(Assertion(sequence: 7, value: 1, origin: 1));
            log.Offer(Assertion(sequence: 2, value: 1, origin: 0));

            log.Tick(0.06f);

            // Neither waits for the other, and neither counts as a straggler against the other:
            // sequences are only comparable within one sender.
            Assert.Equal(2, Drain(log).Count);
            Assert.Equal(0, log.Skipped);
        }

        [Fact]
        public void PassesAnUnstampedAssertionStraightThrough()
        {
            AssertionLog log = new(settleSeconds: 0.05f);

            // A catch-up record asserts a value with no moment behind it, and an older peer does not
            // number its writes at all. Neither may wait out a window it is not part of.
            log.Offer(Assertion(sequence: 5, value: 1));
            log.Offer(Assertion(sequence: 0, value: 1));

            Assert.Equal(new uint[] { 0 }, Drain(log));
        }

        /// <summary>
        /// The bandit mine's opening beat, as it was actually measured, expressed as data.
        ///
        /// From the 2026-08-09 session: the capture dialogue writes its stage flags several frames
        /// before the node that sets the quest state, because DS_SetGBoolNode yields two frames per
        /// node. The build before this one dropped every one of those flags on the receiving machine,
        /// because its quest state had not reached IsRunning yet - and nothing ever re-delivered them.
        /// The measured gap was between 0.685 s and 5.151 s across five sessions, in both role
        /// assignments, so it was never a race that could be won by being lucky.
        ///
        /// Nothing here may be lost, and the quest state must not arrive before the flags that
        /// preceded it.
        /// </summary>
        [Fact]
        public void MineOpeningBeat_DeliversEveryValueInAuthoredOrder()
        {
            AssertionLog log = new(settleSeconds: 0.05f);

            // Sent in the order the postfixes fire, which is inverted for anything a listener caused.
            log.Offer(Assertion(sequence: 2, value: 1));  // BanditMineBrutus_Day01.Captured
            log.Offer(Assertion(sequence: 3, value: 1));  // BanditMineBrutus_DailyRoutines.Day1
            log.Offer(Assertion(sequence: 4, value: 1));  // BanditMineBrutus_DailyRoutines.Working
            log.Offer(Assertion(sequence: 1, value: 1));  // QuestVars_BanditMineBrutus.Debug_StartSequenz
            log.Offer(Assertion(sequence: 5, value: 2));  // QuestVars_BanditMineBrutus.QuestState = IsRunning

            log.Tick(0.06f);

            List<uint> delivered = Drain(log);

            Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, delivered);
            Assert.Equal(0, log.Skipped);
            Assert.Equal(0, log.Pending);
        }

        private static GVarChangeMessage Assertion(uint sequence, int value, byte origin = 0)
        {
            return new GVarChangeMessage
            {
                ListGuid = "list",
                VarId = "var" + sequence,
                Kind = GVarChangeMessage.KindBool,
                IntValue = value,
                Origin = origin,
                Sequence = sequence,
            };
        }

        private static List<uint> Drain(AssertionLog log)
        {
            List<uint> taken = new();

            while (log.TryTake(out GVarChangeMessage message))
            {
                taken.Add(message.Sequence);
            }

            return taken;
        }
    }

    /// <summary>
    /// The wire contract for the messages this round added, and for the fields it added to an
    /// existing one.
    /// </summary>
    public class GVarWireTests
    {
        [Fact]
        public void GVarChange_RoundTripsTheOrderStampAndThePreviousValue()
        {
            GVarChangeMessage sent = new()
            {
                ListGuid = "8f21c0de",
                VarId = "Captured",
                Kind = GVarChangeMessage.KindBool,
                IntValue = 1,
                Origin = 3,
                Sequence = 4211,
                PreviousIntValue = 0,
            };

            GVarChangeMessage received = RoundTrip(sent);

            Assert.Equal("8f21c0de", received.ListGuid);
            Assert.Equal("Captured", received.VarId);
            Assert.Equal(GVarChangeMessage.KindBool, received.Kind);
            Assert.Equal(1, received.IntValue);
            Assert.Equal(3, received.Origin);
            Assert.Equal(4211u, received.Sequence);
            Assert.Equal(0, received.PreviousIntValue);
        }

        [Fact]
        public void GVarChange_RoundTripsAFloatAndItsPreviousValue()
        {
            GVarChangeMessage sent = new()
            {
                ListGuid = "list",
                VarId = "var",
                Kind = GVarChangeMessage.KindFloat,
                FloatValue = 0.25f,
                PreviousFloatValue = 0.75f,
                Sequence = 1,
            };

            GVarChangeMessage received = RoundTrip(sent);

            Assert.Equal(0.25f, received.FloatValue);
            Assert.Equal(0.75f, received.PreviousFloatValue);
        }

        [Fact]
        public void GVarChange_StillBoundsAGuidFromAStranger()
        {
            GVarChangeMessage sent = new()
            {
                ListGuid = new string('g', GVarChangeMessage.MaxGuidLength + 100),
                VarId = new string('v', GVarChangeMessage.MaxIdLength + 100),
                Sequence = 1,
            };

            GVarChangeMessage received = RoundTrip(sent);

            Assert.Equal(GVarChangeMessage.MaxGuidLength, received.ListGuid.Length);
            Assert.Equal(GVarChangeMessage.MaxIdLength, received.VarId.Length);
        }

        [Fact]
        public void GVarSnapshotEnd_RoundTripsItsCount()
        {
            GVarSnapshotEndMessage received = RoundTrip(new GVarSnapshotEndMessage { RecordCount = 612 });

            Assert.Equal(612u, received.RecordCount);
        }

        [Fact]
        public void GVarResyncRequest_RoundTripsReasonAndDepth()
        {
            GVarResyncRequestMessage received = RoundTrip(new GVarResyncRequestMessage
            {
                Reason = GVarResyncRequestMessage.ReasonSavegameLoaded,
                ReplayUnchanged = 1,
            });

            Assert.Equal(GVarResyncRequestMessage.ReasonSavegameLoaded, received.Reason);
            Assert.Equal(1, received.ReplayUnchanged);
        }

        private static T RoundTrip<T>(in T message) where T : struct, INetMessage
        {
            using MemoryStream stream = new();
            message.Write(new BufferWriter(stream));

            stream.Position = 0;
            T received = default;
            received.Read(new BufferReader(stream));

            return received;
        }
    }
}
