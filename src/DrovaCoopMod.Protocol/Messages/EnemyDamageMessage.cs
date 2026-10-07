using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A blow a client landed on a creature the host owns. Carries the unmitigated figure so only the
    /// host's copy applies armor and resistances.
    /// </summary>
    public struct EnemyDamageMessage : INetMessage
    {
        /// <summary>
        /// The attacker runs a scripted brawl against this creature, so the host, which has no brawl, must not
        /// let the blow kill.
        /// </summary>
        public const byte FlagBrawl = 1;

        /// <summary>Who struck it. The host overwrites a client's claim with that peer's assigned id.</summary>
        public PlayerId Attacker;

        /// <summary>Which creature.</summary>
        public EnemyNetId NetId;

        /// <summary>Counts up per attacker, so the host can drop a resend.</summary>
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

            // Clamped at both ends, the figure comes from an untrusted peer.
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
