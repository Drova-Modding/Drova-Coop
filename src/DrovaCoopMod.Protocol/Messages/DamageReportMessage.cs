using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One player to another: a swing of mine landed on you, and this is how hard it was.
    ///
    /// The body a player has in somebody else's world is write-protected - its health only ever arrives
    /// over the wire - so a swing that connects there is calculated and then thrown away. This message is
    /// what makes it real, and it is deliberately the only thing that does: each player stays the one
    /// authority on their own health, which is what keeps two machines from writing the same number and
    /// disagreeing about the result.
    ///
    /// **The figure is the blow, not the wound.** What travels is the attack before the defense rather
    /// than the damage after it. The attacker's copy of the victim mirrors their equipment and nothing
    /// else - not their talents, not their status effects, and it carries a stamina pool it was given for
    /// free - so a total worked out over there is worked out against a half-right character. Sending what
    /// the blow was worth and letting the victim's own game resist, block and mitigate it means armor,
    /// guard and the revive that catches a killing blow each run exactly once, on the machine that holds
    /// the real numbers.
    ///
    /// Reliable, unlike player state: a swing that goes missing is a swing that never happened, and
    /// nothing sent later supersedes it.
    /// </summary>
    public struct DamageReportMessage : INetMessage
    {
        /// <summary>
        /// Who swung. Filled in by the sender and then overwritten by the host with the id it assigned
        /// that peer, for the same reason player state is: otherwise any client could describe a blow
        /// struck by somebody else.
        /// </summary>
        public PlayerId Attacker;

        /// <summary>
        /// Whose body was struck. The only player who will act on this - everyone else drops it, and the
        /// host routes rather than reads.
        /// </summary>
        public PlayerId Victim;

        /// <summary>
        /// How hard, as a positive number, before the victim's own defense is applied to it.
        ///
        /// A <c>ushort</c> because the value is a single blow rather than a total, and nothing a player
        /// can swing comes near five figures. A sender that manages it anyway is capped rather than
        /// wrapped, so an overflow cannot arrive as a scratch.
        /// </summary>
        public ushort Amount;

        /// <summary>
        /// Which blow this is, counting up per attacker for the length of a session.
        ///
        /// Damage is the one thing in this protocol that is neither idempotent nor monotonic on its own:
        /// applying the same report twice takes the health twice. The transport does not reject a repeated
        /// packet - that is a deliberate decision in the API, because rejecting counters would break the
        /// unreliable channels - so the guard has to be here. A receiver keeps the highest number it has
        /// seen from each attacker and ignores anything that does not exceed it.
        /// </summary>
        public uint Sequence;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Attacker.Value);
            writer.Put(Victim.Value);
            writer.Put(Amount);
            writer.Put(Sequence);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Attacker = new PlayerId(reader.GetByte());
            Victim = new PlayerId(reader.GetByte());
            Amount = reader.GetUShort();
            Sequence = reader.GetUInt();
        }
    }
}
