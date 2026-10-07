using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Client to host: a player entered or left a region, so the host rolls its ambush for a lone teammate too.
    /// </summary>
    public struct RegionMessage : INetMessage
    {
        /// <summary>The region, as the API's <c>Region</c> enum value.</summary>
        public byte Region;

        /// <summary>1 when entered, 0 when left.</summary>
        public byte Entered;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Region);
            writer.Put(Entered);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Region = reader.GetByte();
            Entered = reader.GetByte();
        }
    }
}
