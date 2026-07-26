namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Who a player is, for the length of one session, agreed by everyone in it.
    ///
    /// A distinct type rather than a bare number because the alternative already caused a bug waiting to
    /// happen: a transport peer id is <em>machine-local</em> - through a relay it is the relay's virtual
    /// id, so a client sees the host as 0 while the host sees that client as 1 - and it therefore cannot
    /// go on the wire. Both are small integers, both start at 0, and nothing but the compiler is going to
    /// notice one being used where the other belongs.
    ///
    /// The host assigns these and is the only authority on them. A client is told its own in the
    /// handshake result and learns the rest from the roster.
    /// </summary>
    public readonly struct PlayerId : IEquatable<PlayerId>
    {
        /// <summary>
        /// The host is always 0, matching the relay's own convention for the session owner.
        /// </summary>
        public static readonly PlayerId Host = new(0);

        /// <summary>
        /// Not a player. Used before the host has assigned one, so "unassigned" is never confused with
        /// "the host" - which is exactly what a plain zero default would have meant.
        /// </summary>
        public static readonly PlayerId None = new(byte.MaxValue);

        /// <summary>
        /// Creates an id from its wire value.
        /// </summary>
        public PlayerId(byte value)
        {
            Value = value;
        }

        /// <summary>
        /// The value as it travels. A byte because a session holds a handful of players and this rides
        /// along with every state message; the relay's own virtual ids are byte-sized for the same reason.
        /// </summary>
        public byte Value { get; }

        /// <summary>Whether this is a real assigned id.</summary>
        public bool IsAssigned => Value != None.Value;

        /// <summary>Whether this is the host.</summary>
        public bool IsHost => Value == Host.Value;

        /// <inheritdoc/>
        public bool Equals(PlayerId other) => Value == other.Value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is PlayerId other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => Value;

        /// <inheritdoc/>
        public override string ToString() => IsAssigned ? "player " + Value : "unassigned";

        /// <summary>Equality.</summary>
        public static bool operator ==(PlayerId left, PlayerId right) => left.Equals(right);

        /// <summary>Inequality.</summary>
        public static bool operator !=(PlayerId left, PlayerId right) => !left.Equals(right);
    }
}
