namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's handle for one creature, for the messages that travel too often to carry a guid.
    ///
    /// Two bytes instead of thirty-eight. A guid names a creature in a way both machines can resolve
    /// from the scene they both loaded, which is what makes it right for the rare, reliable messages - a
    /// death, a bind. It is far too wide for anything sent per second per creature, and the host has
    /// already established a shared vocabulary by the time those are flowing.
    ///
    /// Grow-only, never reused within a session. Recycling an id is the one mistake that makes an
    /// entity protocol fail in a way nobody can reproduce: an unreliable packet already in flight lands on
    /// whichever creature inherited its number, and the symptom is a wolf across the map taking damage
    /// nobody dealt it. Running out is handled by leaving those creatures unsynchronized and saying so,
    /// which is a visible degradation rather than a silent corruption.
    /// </summary>
    public readonly struct EnemyNetId : IEquatable<EnemyNetId>
    {
        /// <summary>No creature. What an unbound or expired handle reads as.</summary>
        public static readonly EnemyNetId None = new(ushort.MaxValue);

        /// <summary>
        /// The highest id a host will hand out, one below the sentinel.
        /// </summary>
        public const ushort MaxAssignable = ushort.MaxValue - 1;

        /// <summary>
        /// Wraps a raw handle.
        /// </summary>
        /// <param name="value">The number on the wire.</param>
        public EnemyNetId(ushort value)
        {
            Value = value;
        }

        /// <summary>The number on the wire.</summary>
        public ushort Value { get; }

        /// <summary>Whether this names a creature at all.</summary>
        public bool IsSome => Value != ushort.MaxValue;

        /// <inheritdoc/>
        public bool Equals(EnemyNetId other) => Value == other.Value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is EnemyNetId other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => Value;

        /// <inheritdoc/>
        public override string ToString() => IsSome ? "enemy " + Value : "no enemy";

        /// <summary>Whether two handles name the same creature.</summary>
        public static bool operator ==(EnemyNetId left, EnemyNetId right) => left.Equals(right);

        /// <summary>Whether two handles name different creatures.</summary>
        public static bool operator !=(EnemyNetId left, EnemyNetId right) => !left.Equals(right);
    }
}
