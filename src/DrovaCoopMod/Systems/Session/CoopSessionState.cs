namespace DrovaCoopMod.Systems.Session
{
    /// <summary>Where the local player is in getting into a session. The host answers asynchronously.</summary>
    public enum CoopSessionState
    {
        /// <summary>No session, nothing pending.</summary>
        Idle,

        /// <summary>A join was requested and the host has not answered yet.</summary>
        Connecting,

        /// <summary>Hosting. Others may join.</summary>
        Hosting,

        /// <summary>Joined someone else's session.</summary>
        Joined,

        /// <summary>The last attempt failed. <see cref="CoopSession.LastError"/> says why.</summary>
        Failed,
    }
}
