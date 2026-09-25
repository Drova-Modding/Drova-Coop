using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A client asking the host to turn the world over, because their player just slept.
    ///
    /// A client may not advance its own day - see the clock guard - and for good reason: the whole
    /// day-forward cascade would run in one world and nowhere else, and the number it moved is the only
    /// part of it that can be pulled back. But a refusal alone leaves the mine's bed unusable from the
    /// far end of a session: the sleep scene plays, the day's flags cross, and neither world ever wakes
    /// up tomorrow.
    ///
    /// So the refusal reports instead. The host sleeps for real, and nothing has to be granted back:
    /// the host's clock report already crosses periodically, and a leap that size is followed as a
    /// sleep by every client including the one that asked. That is what makes this safe on a queue
    /// which keeps only the newest message - there is no grant to lose, only a clock that is eventually
    /// right.
    ///
    /// The host is not obliged. It checks the ask for itself: forward only, sleep-sized, no further
    /// than a night, and not again for a while. A day cannot be un-advanced, so the one thing this must
    /// never do is let a confused client turn somebody else's world over twice.
    /// </summary>
    public struct DayAdvanceRequestMessage : INetMessage
    {
        /// <summary>Who asked. Stamped by the host from the peer, never trusted from the wire.</summary>
        public PlayerId Player;

        /// <summary>The time they were trying to wake up at, as total game seconds since day zero.</summary>
        public double TargetTotalSeconds;

        /// <summary>
        /// Set when a dialogue set the time rather than a bed: the story's own "it is now evening",
        /// which may be a small step or even earlier in the day, and is granted as authored.
        /// </summary>
        public bool Scripted;

        /// <summary>
        /// Whether the numbers could have come from this game at all, before anything acts on them.
        /// </summary>
        public readonly bool IsPlausible()
        {
            if (double.IsNaN(TargetTotalSeconds) || double.IsInfinity(TargetTotalSeconds)) return false;

            // Negative is before day zero, and the upper bound is far past any real playthrough while
            // still refusing a wild number outright.
            return TargetTotalSeconds >= 0d && TargetTotalSeconds < 100_000d * 86_400d;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Player.Value);
            writer.Put(TargetTotalSeconds);
            writer.Put(Scripted);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Player = new PlayerId(reader.GetByte());
            TargetTotalSeconds = reader.GetDouble();
            Scripted = reader.GetBool();
        }
    }
}
