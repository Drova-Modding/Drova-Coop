using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A client asking the host which of these creatures its world has killed. The client loads everything alive,
    /// since death latches and only killing can be corrected afterward. Asked in batches as bodies stream in.
    /// </summary>
    public struct WorldStateRequestMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>How many guids fit in one request, so a full batch stays inside a datagram.</summary>
        public const int MaxGuids = WireStrings.MaxGuids;

        /// <summary>Which creatures the client wants a verdict on. Never null after a read.</summary>
        public string[] Guids;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            WireStrings.PutStrings(writer, Guids, MaxGuids, MaxGuidLength);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            // Bounded on read too, since the count drives the host's allocations and save lookups.
            Guids = WireStrings.GetStrings(reader, MaxGuids, MaxGuidLength);
        }
    }
}
