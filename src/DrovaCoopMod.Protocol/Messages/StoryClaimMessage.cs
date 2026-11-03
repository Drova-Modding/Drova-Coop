using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A story scene runs in the sender's world, started from this dialogue tree. Repeated while it lasts.
    /// The others hide that tree in their dialogue hubs until a while after the last repeat.
    /// </summary>
    public struct StoryClaimMessage : INetMessage
    {
        /// <summary>The longest tree name accepted.</summary>
        public const int MaxTreeLength = 96;

        /// <summary>The dialogue tree asset's name.</summary>
        public string Tree;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(Tree, MaxTreeLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Tree = WireStrings.Cut(reader.GetString(), MaxTreeLength);
        }
    }
}
