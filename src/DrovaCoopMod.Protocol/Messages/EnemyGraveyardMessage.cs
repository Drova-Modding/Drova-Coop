using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Every guid the host knows to be dead, chunked, sent to a client that just joined.
    /// The game treats a creature with no save record as alive.
    /// </summary>
    public struct EnemyGraveyardMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>How many guids fit in one chunk.</summary>
        public const int MaxGuids = WireStrings.MaxGuids;

        /// <summary>The dead, by guid. Never null after a read.</summary>
        public string[] Guids;

        /// <summary>Non-zero when more chunks follow.</summary>
        public byte More;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            WireStrings.PutStrings(writer, Guids, MaxGuids, MaxGuidLength);

            writer.Put(More);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            // Bounded on read too, since the count decides how much this machine allocates.
            Guids = WireStrings.GetStrings(reader, MaxGuids, MaxGuidLength);

            More = reader.GetByte();
        }
    }
}
