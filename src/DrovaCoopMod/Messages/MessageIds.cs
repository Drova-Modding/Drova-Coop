namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The wire contract: every message type this mod sends, and the id it travels under. Ids are what
    /// both ends agree on, not registration order, so they are declared once here and never reused for a
    /// different type - an id that changes meaning between two mod versions is a corrupted packet that
    /// still passes every check.
    ///
    /// Ids 0 to 255 are available. Leave gaps rather than renumbering.
    /// </summary>
    internal static class MessageIds
    {
        /// <summary>
        /// Client to host, first thing after joining: who this is and what version they run.
        /// </summary>
        internal const ushort Handshake = 1;

        /// <summary>
        /// Host to client, the verdict on that handshake.
        /// </summary>
        internal const ushort HandshakeResult = 2;

        /// <summary>
        /// Where a player is. Sent continuously, so it is unreliable by design.
        /// </summary>
        internal const ushort PlayerState = 10;
    }
}
