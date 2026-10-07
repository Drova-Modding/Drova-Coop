using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>Host to everyone already in: a player joined. The newcomer gets <see cref="RosterSnapshotMessage"/> instead.</summary>
    public struct PlayerJoinedMessage : INetMessage
    {
        /// <summary>The id the host assigned them.</summary>
        public PlayerId Id;

        /// <summary>What to call them.</summary>
        public string Name;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Id.Value);
            writer.Put(WireStrings.Cut(Name, HandshakeMessage.MaxPlayerNameLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Id = new PlayerId(reader.GetByte());
            Name = WireStrings.Cut(reader.GetString(), HandshakeMessage.MaxPlayerNameLength);
        }
    }
}
