using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to everyone still in: a player left. Carries only the id, because whoever receives this
    /// already knows the name from the roster or the join that preceded it.
    /// </summary>
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
