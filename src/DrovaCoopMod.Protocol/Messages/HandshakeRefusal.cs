namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Why a host turned a client away.
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
