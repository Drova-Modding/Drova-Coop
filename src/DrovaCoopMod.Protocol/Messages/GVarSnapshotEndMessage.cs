using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to one client: the join catch-up is complete, so the stored values may now run their consequences.
    /// </summary>
    public struct GVarSnapshotEndMessage : INetMessage
    {
        /// <summary>How many records the host sent. Only logged, to compare against what arrived.</summary>
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
