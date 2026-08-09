using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to one client: the join catch-up is complete.
    ///
    /// The catch-up is applied in two halves - every value stored first, every consequence run
    /// afterwards - because a quest graph that wakes up halfway through a migrating world reads a
    /// state no authored playthrough could produce. That split needs a real terminator. The build
    /// before this one inferred the end from thirty frames without a record, which cannot tell an
    /// empty queue from a per-frame budget that ran out, and duly settled the catch-up early.
    ///
    /// <see cref="RecordCount"/> is what the sender believes it sent. It decides nothing; it is there
    /// so one log line on the receiver says whether the two agree.
    /// </summary>
    public struct GVarSnapshotEndMessage : INetMessage
    {
        /// <summary>How many variable records the host sent in this catch-up.</summary>
        public uint RecordCount;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(RecordCount);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            RecordCount = reader.GetUInt();
        }
    }
}
