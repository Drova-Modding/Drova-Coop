using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to everyone: the session rules changed.
    ///
    /// The same rules ride along with <see cref="HandshakeResultMessage"/>, because a joiner needs them
    /// before it has a body, and this message exists for the rest of the session. Both carry the whole set
    /// rather than a delta - there is one rule today, and a set that is always complete cannot get out of
    /// step with itself if a packet is lost or a client joins between two changes.
    ///
    /// Reliable, and sent only when something actually changed. This is not state to be sampled; a rule
    /// that arrives late is wrong for exactly as long as it takes to arrive, and one that never arrives
    /// leaves two machines disagreeing about who can hurt whom.
    /// </summary>
    public struct SessionRulesMessage : INetMessage
    {
        /// <summary>
        /// Whether players may damage each other in this session, as the host has it set.
        ///
        /// A session rule, not a local preference, which is why only the host's answer counts. Two machines
        /// that disagreed would not disagree symmetrically: the one with it switched on would make its
        /// player's body a legal target while its own player stayed untouchable, so the setting would
        /// decide the fight rather than the players.
        /// </summary>
        public bool FriendlyFire;

        /// <summary>
        /// How much health a player gets back when they stand up after being downed, as a percentage of
        /// their maximum.
        ///
        /// A session rule for the same reason friendly fire is one. Whoever recovered to more health would
        /// win fights they should have lost, and neither player could see why.
        ///
        /// A percentage rather than a figure, because maximum health differs between characters and the
        /// point is the fraction of a fight you have left. Zero means the host never set it. The receiver
        /// substitutes its own default rather than standing a player up on nothing.
        /// </summary>
        public byte RecoveryHealthPercent;

        /// <summary>
        /// How tough creatures are in this session, as a percentage of their authored health.
        ///
        /// A session rule because a creature is one fight for the whole party. Both machines scale the
        /// blows they resolve, so the figure has to be the same on every machine or the same creature
        /// takes a different beating depending on who is nearest.
        ///
        /// Zero means the host never set it. The receiver substitutes the protocol default, which is the
        /// game as shipped.
        /// </summary>
        public ushort EnemyHealthPercent;

        /// <summary>
        /// The host's loot rule, as <see cref="CoopProtocol.LootModeInstanced"/> or
        /// <see cref="CoopProtocol.LootModeShared"/>. Anything else reads as instanced, which is the
        /// game as shipped and the safe answer for a host that never said.
        /// </summary>
        public byte LootMode;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(FriendlyFire);
            writer.Put(RecoveryHealthPercent);
            writer.Put(EnemyHealthPercent);
            writer.Put(LootMode);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            FriendlyFire = reader.GetBool();
            RecoveryHealthPercent = reader.GetByte();
            EnemyHealthPercent = reader.GetUShort();

            byte lootMode = reader.GetByte();
            LootMode = lootMode == CoopProtocol.LootModeShared ? CoopProtocol.LootModeShared : CoopProtocol.LootModeInstanced;
        }
    }
}
