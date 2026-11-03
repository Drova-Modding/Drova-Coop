using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One player to another: a blow of mine landed on your body, before your defense.
    /// The victim's own game resists and applies it, so each player stays the only authority on their own health.
    /// </summary>
    public struct DamageReportMessage : INetMessage
    {
        /// <summary>Who swung. Overwritten by the host with the sender's assigned id.</summary>
        public PlayerId Attacker;

        /// <summary>Whose body was struck. Only that player acts on it.</summary>
        public PlayerId Victim;

        /// <summary>Blow strength before the victim's defense. Senders cap rather than wrap.</summary>
        public ushort Amount;

        /// <summary>
        /// Per-attacker counter for the session. Receivers ignore anything not above the highest seen,
        /// because the transport does not drop repeats and damage is not idempotent.
        /// </summary>
        public uint Sequence;

        /// <summary>Poise the blow was worth as a positive magnitude, or zero.</summary>
        public float PoiseDamage;

        /// <summary>
        /// One when the blow may open a wound. The victim's game computes the bleed itself,
        /// since the damage behavior that would apply it is suppressed on puppeted bodies.
        /// </summary>
        public byte AppliesBleed;

        /// <summary>
        /// <see cref="TypePhysical"/>, <see cref="TypeMagical"/> or <see cref="TypePure"/>.
        /// Only these three resolve by name on the far side.
        /// </summary>
        public byte DamageType;

        /// <summary>
        /// <see cref="OriginPlayer"/>, <see cref="OriginCreature"/> or <see cref="OriginBrawl"/>. Read by the friendly-fire rule,
        /// so host-driven creatures can still hurt clients with friendly fire off.
        /// </summary>
        public byte Origin;

        /// <summary>The creature that struck, for <see cref="OriginCreature"/> and <see cref="OriginBrawl"/>. The victim refuses a blow from no creature near it.</summary>
        public EnemyNetId Creature;

        /// <summary>A blow one player landed on another. Subject to the friendly-fire rule.</summary>
        public const byte OriginPlayer = 0;

        /// <summary>A blow by a creature the sender drives. Never friendly fire.</summary>
        public const byte OriginCreature = 1;

        /// <summary>A blow by an NPC of a scripted brawl the sender runs, which the victim joined. It knocks out, never puts down for good.</summary>
        public const byte OriginBrawl = 2;

        /// <summary>Answered by armor and physical resistance.</summary>
        public const byte TypePhysical = 0;

        /// <summary>Answered by magical resistance.</summary>
        public const byte TypeMagical = 1;

        /// <summary>Resisted by nothing.</summary>
        public const byte TypePure = 2;

        /// <summary>Ceiling on one blow's poise, so a peer cannot empty a pool for good.</summary>
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
            writer.Put(Origin);
            writer.Put(Creature.Value);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Attacker = new PlayerId(reader.GetByte());
            Victim = new PlayerId(reader.GetByte());
            Amount = reader.GetUShort();
            Sequence = reader.GetUInt();

            // Negative would restore stance.
            float poise = reader.GetFloat();
            PoiseDamage = poise > 0f ? (poise < MaxPoiseDamage ? poise : MaxPoiseDamage) : 0f;

            AppliesBleed = reader.GetByte();

            byte type = reader.GetByte();
            DamageType = type is TypePhysical or TypeMagical or TypePure ? type : TypePhysical;

            // A player's blow is the stricter case for friendly fire.
            byte origin = reader.GetByte();
            Origin = origin is OriginCreature or OriginBrawl ? origin : OriginPlayer;

            Creature = new EnemyNetId(reader.GetUShort());
        }
    }
}
