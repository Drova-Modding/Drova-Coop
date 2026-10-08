namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// Host, join and leave in one place for the console, options panel and session window.
    /// Each returns the line to show the player.
    /// </summary>
    internal static class CoopActions
    {
        /// <summary>Starts hosting through the configured relay.</summary>
        internal static string Host()
        {
            // Checked here too so the returned line never claims hosting the session refused.
            string? refusal = VersionCheck.Refusal;
            if (refusal != null) return refusal;

            CoopSession.HostViaRelay(
                CoopPreferences.RelayAddress,
                CoopPreferences.RelayPort,
                CoopPreferences.SessionCodeOrNull(),
                CoopPreferences.PasswordOrNull());

            return $"hosting through {CoopPreferences.RelayAddress}:{CoopPreferences.RelayPort}; run coop_status for the code.";
        }

        /// <summary>Joins a session.</summary>
        /// <param name="code">The code to join, or null to use the configured one.</param>
        internal static string Join(string? code = null)
        {
            code = string.IsNullOrWhiteSpace(code) ? CoopPreferences.SessionCodeOrNull() : code.Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                return "no session code. Use coop_join <code>, or set DrovaCoop.SessionCode.";
            }

            string? refusal = VersionCheck.Refusal;
            if (refusal != null) return refusal;

            CoopSession.JoinViaRelay(
                CoopPreferences.RelayAddress,
                CoopPreferences.RelayPort,
                code,
                CoopPreferences.PasswordOrNull());

            return $"joining '{code}' through {CoopPreferences.RelayAddress}:{CoopPreferences.RelayPort}...";
        }

        /// <summary>Leaves the current session. Safe when there is none.</summary>
        internal static string Leave()
        {
            CoopSession.Leave();
            return "left the session.";
        }
    }
}
