using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The first thing a client sends its host, so a protocol mismatch is caught at the door.
    /// </summary>
    public struct HandshakeMessage : INetMessage
    {
        /// <summary>The longest name accepted. The name arrives from a not-yet-accepted stranger.</summary>
        public const int MaxPlayerNameLength = 32;

        /// <summary>The coop protocol version, not the mod version. Bump it whenever a message's layout changes.</summary>
        public ushort ProtocolVersion;

        /// <summary>What to call this player in logs and the UI.</summary>
        public string PlayerName;

        /// <summary>
        /// A hash of the mods this player runs. On mismatch each side logs its own list. Zero means not computed.
        /// </summary>
        public uint ModSetHash;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(ProtocolVersion);
            writer.Put(WireStrings.Cut(PlayerName, MaxPlayerNameLength));
            writer.Put(ModSetHash);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            ProtocolVersion = reader.GetUShort();

            // Truncate on read too, the sender is not trusted.
            PlayerName = WireStrings.Cut(reader.GetString(), MaxPlayerNameLength);
            ModSetHash = reader.GetUInt();
        }
    }
}
