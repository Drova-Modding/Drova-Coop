using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's verdict on a <see cref="HandshakeMessage"/>, so a refused client learns why before it is dropped.
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

        /// <summary>
        /// The id this client is known by for the session, or <see cref="PlayerId.None"/> when refused.
        /// </summary>
        public PlayerId AssignedPlayerId;

        /// <summary>
        /// Why, when <see cref="Accepted"/> is false.
        /// </summary>
        public HandshakeRefusal Refusal;

        /// <summary>
        /// Whether the host runs the same mods. A mismatch is only a warning, since QoL mods differ harmlessly.
        /// </summary>
        public bool ModSetMatches;

        /// <summary>
        /// The session rules, needed before the joiner has a body. Later changes come as <see cref="SessionRulesMessage"/>.
        /// </summary>
        public SessionRulesMessage Rules;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Accepted);
            writer.Put(HostProtocolVersion);
            writer.Put(AssignedPlayerId.Value);
            writer.Put((byte)Refusal);
            writer.Put(ModSetMatches);
            Rules.Write(writer);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Accepted = reader.GetBool();
            HostProtocolVersion = reader.GetUShort();
            AssignedPlayerId = new PlayerId(reader.GetByte());

            // An unknown value is still a refusal, just not one this build can name.
            byte refusal = reader.GetByte();
            Refusal = Enum.IsDefined(typeof(HandshakeRefusal), refusal) ? (HandshakeRefusal)refusal : HandshakeRefusal.None;

            ModSetMatches = reader.GetBool();
            Rules.Read(reader);
        }
    }
}
