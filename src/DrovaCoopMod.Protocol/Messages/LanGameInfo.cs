using System.Text;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a direct host answers a LAN discovery request with. Not a session message: it travels before any connection.
    /// </summary>
    public readonly record struct LanGameInfo(ushort ProtocolVersion, byte Players, byte MaxPlayers, string HostName)
    {
        /// <summary>The default UDP port of a direct host.</summary>
        public const int DefaultPort = 9061;

        // Tells this mod's answer apart from another mod's on the same port.
        private static readonly byte[] Tag = "DCOP"u8.ToArray();

        /// <summary>Whether a client running <paramref name="localProtocol"/> can join.</summary>
        public bool IsJoinable(ushort localProtocol) => ProtocolVersion == localProtocol && Players < MaxPlayers;

        /// <summary>The bytes for <c>NetworkAccess.SetDiscoveryInfo</c>.</summary>
        public byte[] Encode()
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream, Encoding.UTF8);

            writer.Write(Tag);
            writer.Write(ProtocolVersion);
            writer.Write(Players);
            writer.Write(MaxPlayers);
            writer.Write(WireStrings.Cut(HostName, HandshakeMessage.MaxPlayerNameLength));
            writer.Flush();

            return stream.ToArray();
        }

        /// <summary>Reads what <see cref="Encode"/> wrote. The sender is anyone on the network.</summary>
        public static bool TryDecode(byte[] data, out LanGameInfo info)
        {
            info = default;
            if (data.Length < Tag.Length || !data.AsSpan(0, Tag.Length).SequenceEqual(Tag)) return false;

            try
            {
                using BinaryReader reader = new(new MemoryStream(data, Tag.Length, data.Length - Tag.Length), Encoding.UTF8);
                info = new LanGameInfo(reader.ReadUInt16(), reader.ReadByte(), reader.ReadByte(),
                    WireStrings.Cut(reader.ReadString(), HandshakeMessage.MaxPlayerNameLength));
                return true;
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or FormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// Splits <c>host</c>, <c>host:port</c> or <c>[ipv6]:port</c>. Without a port, and for a bare IPv6 address, the
        /// port is <paramref name="defaultPort"/>.
        /// </summary>
        public static bool TryParseAddress(string? text, int defaultPort, out string host, out int port)
        {
            host = string.Empty;
            port = defaultPort;
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) return false;

            string portText = string.Empty;
            if (text.StartsWith('['))
            {
                int close = text.IndexOf(']');
                if (close < 0) return false;

                host = text[1..close];
                if (close + 1 < text.Length)
                {
                    if (text[close + 1] != ':') return false;
                    portText = text[(close + 2)..];
                }
            }
            else if (text.IndexOf(':') is var colon and >= 0 && text.IndexOf(':', colon + 1) < 0)
            {
                host = text[..colon];
                portText = text[(colon + 1)..];
            }
            else
            {
                host = text;
            }

            if (portText.Length > 0 && (!int.TryParse(portText, out port) || port < 1 || port > 65535)) return false;

            return host.Length > 0;
        }
    }
}
