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

        /// <summary>
        /// The id this client is known by for the rest of the session, or <see cref="PlayerId.None"/>
        /// when it was refused. A client cannot work this out for itself: its own transport peer ids are
        /// local to its machine, so the only way it learns who it is, is being told.
        /// </summary>
        public PlayerId AssignedPlayerId;

        /// <summary>
        /// Why, when <see cref="Accepted"/> is false.
        /// </summary>
        public HandshakeRefusal Refusal;

        /// <summary>
        /// Whether the host is running the same mods. False is a warning, not a refusal: a differing mod
        /// set usually means somebody has a QoL mod, and refusing on it would make the mod unusable
        /// for the people most likely to run it. A protocol version mismatch is the thing worth refusing,
        /// because that one corrupts messages rather than merely differing.
        /// </summary>
        public bool ModSetMatches;

        /// <summary>
        /// The session rules, as the host has them. Delivered here because a joiner needs them before it
        /// has a body to be hit in. The host can change them later with a <see cref="SessionRulesMessage"/>.
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

            // Anything this build does not recognize is still a refusal, just not one it can name.
            byte refusal = reader.GetByte();
            Refusal = Enum.IsDefined(typeof(HandshakeRefusal), refusal) ? (HandshakeRefusal)refusal : HandshakeRefusal.None;

            ModSetMatches = reader.GetBool();
            Rules.Read(reader);
        }
    }
}
