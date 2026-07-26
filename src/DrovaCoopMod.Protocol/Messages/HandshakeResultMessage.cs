using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's verdict on a <see cref="HandshakeMessage"/>. A client that is turned away is told why
    /// before it is dropped, so the player sees "the host runs coop protocol 3, you run 2" instead of a
    /// connection that simply ends.
    /// </summary>
    public struct HandshakeResultMessage : INetMessage
    {
        /// <summary>
        /// Whether the client is in.
        /// </summary>
        public bool Accepted;

        /// <summary>
        /// The host's coop protocol version, so a rejected client can name the difference.
        /// </summary>
        public ushort HostProtocolVersion;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Accepted);
            writer.Put(HostProtocolVersion);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Accepted = reader.GetBool();
            HostProtocolVersion = reader.GetUShort();
        }
    }
}
