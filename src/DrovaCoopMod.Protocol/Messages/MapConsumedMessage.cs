using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The game destroyed an object the API's map layout placed (a sprung trap), so every other machine removes its copy too.
    /// </summary>
    public struct MapConsumedMessage : INetMessage
    {
        /// <summary>The longest id carried.</summary>
        private const int MaxIdLength = 128;

        /// <summary>The placed object's layout id, the same on every machine with the same layout files.</summary>
        public string Id;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(Id, MaxIdLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Id = WireStrings.Cut(reader.GetString(), MaxIdLength);
        }
    }
}
