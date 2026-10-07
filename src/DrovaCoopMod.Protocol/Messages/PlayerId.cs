namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A session-wide player id assigned by the host. Kept distinct from the machine-local transport peer id, which cannot go on the wire.
    /// </summary>
    public readonly record struct PlayerId
    {
        /// <summary>The host, always 0.</summary>
        public static readonly PlayerId Host = new(0);

        /// <summary>
        /// Not yet assigned. Distinct from the host's 0.
        /// </summary>
        public static readonly PlayerId None = new(byte.MaxValue);

        /// <summary>
        /// Creates an id from its wire value.
        /// </summary>
        public PlayerId(byte value)
        {
            Value = value;
        }

        /// <summary>The wire value.</summary>
        public byte Value { get; }

        /// <summary>Whether this is a real assigned id.</summary>
        public bool IsAssigned => Value != None.Value;

        /// <summary>Whether this is the host.</summary>
        public bool IsHost => Value == Host.Value;

        /// <inheritdoc/>
        public override string ToString() => IsAssigned ? "player " + Value : "unassigned";
    }
}
