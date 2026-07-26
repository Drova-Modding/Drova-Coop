namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Why a host turned a client away, so the client can say something true about it.
    ///
    /// Without this every refusal reads as a version mismatch, which sends a player who could not get in
    /// because the session was full off checking their mod versions.
    /// </summary>
    public enum HandshakeRefusal : byte
    {
        /// <summary>Not refused.</summary>
        None = 0,

        /// <summary>The two sides speak different coop protocol versions.</summary>
        ProtocolMismatch = 1,

        /// <summary>The session already holds as many players as it accepts.</summary>
        SessionFull = 2,
    }
}
