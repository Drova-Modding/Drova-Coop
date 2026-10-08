using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A map one player unlocked, keyed by the map definition's guid. The game's enable call is repeat-safe.
    /// </summary>
    public struct MapUnlockMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        private const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>The map definition's authored guid.</summary>
        public string Guid;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(Guid, MaxGuidLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Guid = WireStrings.Cut(reader.GetString(), MaxGuidLength);
        }
    }
}
