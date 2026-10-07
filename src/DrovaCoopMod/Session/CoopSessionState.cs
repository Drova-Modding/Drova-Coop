namespace DrovaCoopMod.Session
{
    /// <summary>Where the local player is in getting into a session. The relay answers asynchronously.</summary>
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
