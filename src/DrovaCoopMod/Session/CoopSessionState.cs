namespace DrovaCoopMod.Session
{
    /// <summary>
    /// Where the local player is in the process of getting into a session. A relay session cannot report
    /// success from the call that starts it, so "asked" and "in" have to be separate states.
    /// </summary>
    public enum CoopSessionState
    {
        /// <summary>No session, nothing pending.</summary>
        Idle,

        /// <summary>A host or join was requested and the relay has not answered yet.</summary>
        Connecting,

        /// <summary>Hosting. Others may join with the session code.</summary>
        Hosting,

        /// <summary>Joined someone else's session.</summary>
        Joined,

        /// <summary>The last attempt failed. <see cref="CoopSession.LastError"/> says why.</summary>
        Failed,
    }
}
