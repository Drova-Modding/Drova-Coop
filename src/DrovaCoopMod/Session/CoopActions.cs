namespace DrovaCoopMod.Session
{
    /// <summary>
    /// The three things a player can do to a session - host it, join it, leave it - in one place, so the
    /// console commands, the options panel and the debug hotkeys drive the same code.
    ///
    /// They used to be three copies of the same four lines, which is fine right up to the day one of them
    /// learns something the others do not. Each returns the line to show, rather than logging: the console
    /// wants it echoed to the terminal, the options panel wants it in the log, and neither should have to
    /// reconstruct what happened from a bool.
    /// </summary>
    internal static class CoopActions
    {
        /// <summary>
        /// Starts hosting through the configured relay.
        /// </summary>
        internal static string Host()
        {
            CoopSession.HostViaRelay(
                CoopPreferences.RelayAddress,
                CoopPreferences.RelayPort,
                CoopPreferences.SessionCodeOrNull(),
                CoopPreferences.PasswordOrNull());

            return $"hosting through {CoopPreferences.RelayAddress}:{CoopPreferences.RelayPort}; run coop_status for the code.";
        }

        /// <summary>
        /// Joins a session.
        /// </summary>
        /// <param name="code">
        /// The code to join, or null to use the configured one. A code given here wins, because somebody
        /// who has just been handed one does not expect to edit a settings file first.
        /// </param>
        internal static string Join(string? code = null)
        {
            code = string.IsNullOrWhiteSpace(code) ? CoopPreferences.SessionCodeOrNull() : code.Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                return "no session code. Use coop_join <code>, or set DrovaCoop.SessionCode.";
            }

            CoopSession.JoinViaRelay(
                CoopPreferences.RelayAddress,
                CoopPreferences.RelayPort,
                code!,
                CoopPreferences.PasswordOrNull());

            return $"joining '{code}' through {CoopPreferences.RelayAddress}:{CoopPreferences.RelayPort}...";
        }

        /// <summary>
        /// Leaves the current session. Safe when there is none.
        /// </summary>
        internal static string Leave()
        {
            CoopSession.Leave();
            return "left the session.";
        }
    }
}
