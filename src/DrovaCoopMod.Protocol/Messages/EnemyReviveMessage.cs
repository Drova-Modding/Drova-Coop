using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Creatures the host revived, pushed unprompted to everybody. Unlike <see cref="EnemyAliveMessage"/> it outranks a recorded death.
    /// </summary>
    public struct EnemyReviveMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>How many guids fit in one chunk.</summary>
        public const int MaxGuids = WireStrings.MaxGuids;

        /// <summary>The revived, by guid. Never null after a read.</summary>
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
