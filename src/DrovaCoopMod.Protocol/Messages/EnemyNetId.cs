namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's two-byte handle for one creature, for messages sent too often to carry a guid.
    /// Never reused within a session, so a late unreliable packet cannot land on another creature.
    /// </summary>
    public readonly record struct EnemyNetId
    {
        /// <summary>No creature. What an unbound or expired handle reads as.</summary>
        public static readonly EnemyNetId None = new(ushort.MaxValue);

        /// <summary>The highest id a host will hand out, one below the sentinel.</summary>
        public const ushort MaxAssignable = ushort.MaxValue - 1;

        /// <summary>Wraps a raw handle.</summary>
        public EnemyNetId(ushort value)
        {
            Value = value;
        }

        /// <summary>The number on the wire.</summary>
        public ushort Value { get; }

        /// <summary>Whether this names a creature at all.</summary>
        public bool IsSome => Value != ushort.MaxValue;

        /// <inheritdoc/>
        public override string ToString() => IsSome ? "enemy " + Value : "no enemy";
    }
}
