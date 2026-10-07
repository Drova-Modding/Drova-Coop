using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Creatures a client asked about that the host's save still has alive, by guid since they may never have had a bind.
    /// </summary>
    public struct EnemyAliveMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>How many guids fit in one chunk.</summary>
        public const int MaxGuids = WireStrings.MaxGuids;

        /// <summary>The living, by guid. Never null after a read.</summary>
        public string[] Guids;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            WireStrings.PutStrings(writer, Guids, MaxGuids, MaxGuidLength);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            // Bounded on read too, so the sender cannot choose how much this machine allocates.
            Guids = WireStrings.GetStrings(reader, MaxGuids, MaxGuidLength);
        }
    }
}
