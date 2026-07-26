using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The first thing a client sends its host. Carries the coop protocol version so a version mismatch
    /// is caught at the door rather than showing up later as one side desyncing for no visible reason.
    ///
    /// This one allocates on receive because of the string, which is fine: it is sent once per player per
    /// session, not per frame.
    /// </summary>
    public struct HandshakeMessage : INetMessage
    {
        /// <summary>
        /// The longest name accepted on the wire. A name arrives before the sender has been accepted into
        /// anything, so it is the one field a stranger can put arbitrary bytes in; without a cap the
        /// reader will happily take the rest of the packet and hand it to the log.
        /// </summary>
        public const int MaxPlayerNameLength = 32;

        /// <summary>
        /// The coop protocol version, not the mod version. Bump it whenever a message's layout changes.
        /// </summary>
        public ushort ProtocolVersion;

        /// <summary>
        /// What to call this player in logs and, later, in the UI. Truncated to
        /// <see cref="MaxPlayerNameLength"/> on both sides of the wire.
        /// </summary>
        public string PlayerName;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(ProtocolVersion);
            writer.Put(Truncate(PlayerName ?? string.Empty));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            ProtocolVersion = reader.GetUShort();

            // Truncating on read as well as write, because the sender is not the one to trust about this.
            PlayerName = Truncate(reader.GetString() ?? string.Empty);
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxPlayerNameLength ? value : value[..MaxPlayerNameLength];
        }
    }
}
