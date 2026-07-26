using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to everyone already in: a player joined, and this is who they are. The incremental
    /// counterpart to <see cref="RosterSnapshotMessage"/>, which the newcomer gets instead.
    /// </summary>
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
            writer.Put(RosterEntry.TruncateName(Name ?? string.Empty));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Id = new PlayerId(reader.GetByte());
            Name = RosterEntry.TruncateName(reader.GetString() ?? string.Empty);
        }
    }
}
