namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The wire contract: every message type this mod sends, and the id it travels under. Ids are what
    /// both ends agree on, not registration order, so they are declared once here and never reused for a
    /// different type - an id that changes meaning between two mod versions is a corrupted packet that
    /// still passes every check.
    ///
    /// Ids 0 to 255 are available, and that space is process-global and shared with every other mod that
    /// uses the API's transport. Leave gaps rather than renumbering.
    /// </summary>
    public static class MessageIds
    {
        /// <summary>
        /// Client to host, first thing after joining: who this is and what version they run.
        /// </summary>
        public const ushort Handshake = 1;

        /// <summary>
        /// Host to client, the verdict on that handshake.
        /// </summary>
        public const ushort HandshakeResult = 2;

        /// <summary>
        /// Where a player is. Sent continuously, so it is unreliable by design.
        /// </summary>
        public const ushort PlayerState = 10;
    }
}
