using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A blow a client landed on a creature the host owns.
    ///
    /// The mirror of <see cref="DamageReportMessage"/>, and it carries the same thing for the same reason:
    /// the blow, not the wound. What crosses is the figure before the defender had any say in it, and
    /// the host's own copy of the creature answers it with the armour, resistances and blockers only the
    /// host is authoritative for. A client sending the final number would be mitigating twice - once
    /// against its own copy, once against the host's.
    ///
    /// Sequenced per attacker so a repeat is harmless. Reliable, because a lost blow is damage a player
    /// dealt and never saw land, which reads as the game ignoring them.
    /// </summary>
    public struct EnemyDamageMessage : INetMessage
    {
        /// <summary>
        /// The attacker's machine is running a scripted brawl against this creature. Brawls are
        /// non-lethal by construction - the brawl machinery clamps the loser at three health and knocks
        /// them out instead - but that machinery only runs where the brawl was started. The flag is what
        /// lets the host, who has no brawl, refuse to let the blow kill.
        /// </summary>
        public const byte FlagBrawl = 1;

        /// <summary>
        /// Who struck it. The host overwrites a client's claim with the id it assigned that peer, so a
        /// client cannot report damage as somebody else.
        /// </summary>
        public PlayerId Attacker;

        /// <summary>Which creature.</summary>
        public EnemyNetId NetId;

        /// <summary>
        /// Counts up per attacker. The host ignores anything it has already counted, which is what makes a
        /// resend harmless.
        /// </summary>
        public uint Sequence;

        /// <summary>How much the blow was worth, before the defender's side of the calculation.</summary>
        public ushort Amount;

        /// <summary>The raw stance the blow was worth, resolved by the host against its own copy.</summary>
        public float PoiseDamage;

        /// <summary>Non-zero when the blow may open a wound. The host decides how big.</summary>
        public byte AppliesBleed;

        /// <summary>Physical, magical or pure. See <see cref="DamageReportMessage"/>.</summary>
        public byte DamageType;

        /// <summary>See <see cref="FlagBrawl"/>.</summary>
        public byte Flags;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Attacker.Value);
            writer.Put(NetId.Value);
            writer.Put(Sequence);
            writer.Put(Amount);
            writer.Put(PoiseDamage);
            writer.Put(AppliesBleed);
            writer.Put(DamageType);
            writer.Put(Flags);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Attacker = new PlayerId(reader.GetByte());
            NetId = new EnemyNetId(reader.GetUShort());
            Sequence = reader.GetUInt();
            Amount = reader.GetUShort();

            // Clamped at both ends, like the player report: a figure from a stranger reaches a stance
            // calculation, and neither a negative nor an enormous one has any legitimate sender.
            float poise = reader.GetFloat();
            PoiseDamage = poise > 0f
                ? (poise < DamageReportMessage.MaxPoiseDamage ? poise : DamageReportMessage.MaxPoiseDamage)
                : 0f;

            AppliesBleed = reader.GetByte();

            byte type = reader.GetByte();
            DamageType = type is DamageReportMessage.TypePhysical
                or DamageReportMessage.TypeMagical
                or DamageReportMessage.TypePure
                ? type
                : DamageReportMessage.TypePhysical;

            Flags = reader.GetByte();
        }
    }
}
