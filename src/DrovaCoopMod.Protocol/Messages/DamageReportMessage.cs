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

        /// <summary>
        /// How much poise the blow was worth, as a positive magnitude, or zero when it carried none.
        ///
        /// **Poise is what decides whether a blow staggers**, and like the damage itself it is computed
        /// on the attacker's machine against a body that cannot really be staggered. Without it crossing,
        /// the attacker watches the remote body's stance break while its owner walks on untouched - the
        /// two machines disagreeing about the same blow, which is the thing this message exists to stop.
        /// </summary>
        public float PoiseDamage;

        /// <summary>
        /// One when the blow was the kind that opens a wound, zero otherwise.
        ///
        /// **A flag rather than the bleeding itself.** Bleeding is not a number the attacker can send: the
        /// game works it out from the damage that actually landed, which only the victim's machine knows.
        /// So what crosses is the permission, and the victim's own game applies the effect with its own
        /// figure - the same division of labor as the damage.
        ///
        /// It has to cross explicitly because nothing else carries it. The effect is applied by the
        /// attacking ability's damage behavior, and that behavior is deliberately suppressed on a body
        /// this mod puppets, so a spell that wounds would otherwise take health off its target and never
        /// leave a wound.
        /// </summary>
        public byte AppliesBleed;

        /// <summary>
        /// Which resistance answers the blow, as one of <see cref="TypePhysical"/>,
        /// <see cref="TypeMagical"/> or <see cref="TypePure"/>.
        ///
        /// **Only the three the game names, because those are the three the far side can find.** Damage
        /// types are authored assets with no database to look them up in, so an exotic one cannot be
        /// resolved from an id. These three are reachable by name through the settings handlers, and
        /// between them they cover what actually differs: a spell resisted by wards rather than by armor.
        /// Anything else is sent as physical, which is what every blow used to be.
        /// </summary>
        public byte DamageType;

        /// <summary>Answered by armor and physical resistance. What weapons deal.</summary>
        public const byte TypePhysical = 0;

        /// <summary>Answered by magical resistance. What most spells deal.</summary>
        public const byte TypeMagical = 1;

        /// <summary>Resisted by nothing.</summary>
        public const byte TypePure = 2;

        /// <summary>
        /// The most stance a single blow may be worth.
        ///
        /// A poise pool starts at a hundred, so anything near this is already far beyond what an authored
        /// swing could do. The number is not a model of the game - it is a ceiling, and its job is to stop
        /// a peer sending a magnitude that saturates the pool and leaves its victim staggered by every hit
        /// for the rest of the session with no way back.
        /// </summary>
        public const float MaxPoiseDamage = 1000f;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Attacker.Value);
            writer.Put(Victim.Value);
            writer.Put(Amount);
            writer.Put(Sequence);
            writer.Put(PoiseDamage);
            writer.Put(AppliesBleed);
            writer.Put(DamageType);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Attacker = new PlayerId(reader.GetByte());
            Victim = new PlayerId(reader.GetByte());
            Amount = reader.GetUShort();
            Sequence = reader.GetUInt();

            // Bounded at both ends rather than trusted, which is the rule every other float on this wire
            // follows. Below zero a blow would restore its victim's stance instead of breaking it; above
            // the ceiling it would empty a pool that never refills. Neither is a decision the sender gets
            // to make.
            float poise = reader.GetFloat();
            PoiseDamage = poise > 0f ? (poise < MaxPoiseDamage ? poise : MaxPoiseDamage) : 0f;

            AppliesBleed = reader.GetByte();

            // Anything this build does not know is read as physical rather than guessed at, which is the
            // same answer it would have given before types travelled at all.
            byte type = reader.GetByte();
            DamageType = type is TypePhysical or TypeMagical or TypePure ? type : TypePhysical;
        }
    }
}
