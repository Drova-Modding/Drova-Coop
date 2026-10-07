using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>Host to everyone still in: a player left.</summary>
    public struct PlayerLeftMessage : INetMessage
    {
        /// <summary>Who left.</summary>
        public PlayerId Id;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Id.Value);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Id = new PlayerId(reader.GetByte());
        }
    }
}
