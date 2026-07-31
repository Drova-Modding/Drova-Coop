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
        /// Whether players may damage each other in this session, as the host has it set.
        ///
        /// A session rule, not a local preference, which is why it travels with the acceptance rather
        /// than being read on each machine. Two machines that disagreed would not disagree symmetrically:
        /// the one with it switched on would make its player's body a legal target while its own player
        /// stayed untouchable, so the setting would decide the fight rather than the players.
        ///
        /// Delivered here because a joiner needs it before it has a body to be hit in, and there is no
        /// earlier moment to deliver it. It is not the last word: the host can change the rule while the
        /// session runs, and says so with a <see cref="SessionRulesMessage"/>. Both carry the whole rule
        /// rather than a change to it, so whichever arrives last is simply right.
        /// </summary>
        public bool FriendlyFire;

        /// <summary>
        /// How much health a player gets back when they stand up after being downed, as a percentage of
        /// their maximum.
        ///
        /// A session rule for the same reason friendly fire is one. Two machines that disagreed would
        /// not disagree harmlessly: whoever recovered to more health would win fights they should have
        /// lost, and neither player could see why. Being downed is meant to cost something, and how much
        /// it costs is one answer for the whole session.
        ///
        /// A percentage rather than a figure, because maximum health differs between characters and the
        /// point is the fraction of a fight you have left. Zero means the host never set it; the receiver
        /// substitutes its own default rather than standing a player up on nothing.
        /// </summary>
        public byte RecoveryHealthPercent;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Accepted);
            writer.Put(HostProtocolVersion);
            writer.Put(AssignedPlayerId.Value);
            writer.Put((byte)Refusal);
            writer.Put(ModSetMatches);
            writer.Put(FriendlyFire);
            writer.Put(RecoveryHealthPercent);
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
            FriendlyFire = reader.GetBool();
            RecoveryHealthPercent = reader.GetByte();
        }
    }
}
