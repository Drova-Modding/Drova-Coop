using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A client's player entered or left a region (the game's area names: Forest, Ruins, ...). Client to host, so the
    /// host rolls a region's ambush for a teammate standing in it alone too.
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
