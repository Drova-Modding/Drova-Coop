using DrovaCoopMod.Versioning;
using MelonLoader;

namespace DrovaCoopMod
{
    /// <summary>
    /// Fetches the repository's <c>VERSION</c> file once at start and holds what it says about this
    /// build. See <see cref="VersionPolicy"/> for the file.
    ///
    /// This is the remote kill switch. A build that turns out to corrupt saves is already on players'
    /// machines by the time anyone knows, and every session it hosts writes into somebody's world.
    /// Naming it in the file stops it from hosting or joining from the next start on, without a release.
    ///
    /// Fails open, deliberately. No network, no file, a typo in the file: each reads as allowed, because
    /// a player on a plane must still be able to host, and the file exists to stop known-bad builds, not
    /// to make good ones depend on a web server. The fetch runs on a thread pool thread so start is not
    /// held; a session begun before the answer arrives is allowed and the answer is logged when it comes.
    /// </summary>
    internal static class VersionCheck
    {
        /// <summary>Where the file lives when the preference does not say otherwise.</summary>
        internal const string DefaultUrl = "https://raw.githubusercontent.com/Drova-Modding/Drova-Coop/master/VERSION";

        /// <summary>Where a player is sent for a newer build.</summary>
        internal const string ReleasesUrl = "https://github.com/Drova-Modding/Drova-Coop/releases";

        private const int TimeoutSeconds = 10;

        private static VersionVerdict? _verdict;

        private static string _summary = "not checked";

        private static bool _started;

        /// <summary>The build this is, as the version the melon reports.</summary>
        internal static Version Running { get; } = ParseRunning();

        /// <summary>
        /// The sentence that refuses hosting and joining, or null while this build is allowed. Null
        /// also before the fetch has answered and when it never will.
        /// </summary>
        internal static string? Refusal
        {
            get
            {
                VersionVerdict? verdict = _verdict;
                if (verdict == null || verdict.Value.Allowed) return null;

                return verdict.Value.Refusal + " Get " + (verdict.Value.Latest != null ? VersionPolicy.Format(verdict.Value.Latest) : "the current build") + " from " + ReleasesUrl + ".";
            }
        }

        /// <summary>One line on what the check found, for <c>coop_status</c>.</summary>
        internal static string Summary => _summary;

        /// <summary>
        /// Starts the fetch. Called once at melon init; a second call does nothing.
        /// </summary>
        internal static void Start()
        {
            if (_started) return;
            _started = true;

            string url = CoopPreferences.VersionCheckUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                _summary = "off (VersionCheckUrl is empty)";
                MelonLogger.Msg("[Version] The version check is off; this build will not learn if it has been withdrawn.");
                return;
            }

            _summary = "checking " + url;
            Task.Run(() => Fetch(url.Trim()));
        }

        private static async Task Fetch(string url)
        {
            string text;

            try
            {
                using HttpClient client = new();
                client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DrovaCoop/" + VersionPolicy.Format(Running));

                text = await client.GetStringAsync(url).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Allowed, and said so. The only build this could wrongly let through is one the file
                // names, and a player who cannot reach the file cannot reach a newer build either.
                _summary = "unreachable, so allowed: " + e.Message;
                MelonLogger.Warning("[Version] Could not read the version policy, so this build is allowed: " + e.Message);
                return;
            }

            Judge(text);
        }

        /// <summary>
        /// Applies a policy text to this build. Separate from the fetch so a test or a console command
        /// can hand it a file directly.
        /// </summary>
        private static void Judge(string text)
        {
            VersionPolicy policy = VersionPolicy.Parse(text);
            VersionVerdict verdict = policy.Judge(Running);

            foreach (string line in policy.Ignored)
            {
                MelonLogger.Warning("[Version] A line in the version policy was not understood and is ignored: " + line);
            }

            _verdict = verdict;

            if (!verdict.Allowed)
            {
                _summary = "REFUSED: " + verdict.Refusal;
                MelonLogger.Error("[Version] " + Refusal);
                return;
            }

            if (verdict.NewerAvailable && verdict.Latest != null)
            {
                _summary = $"allowed; {VersionPolicy.Format(verdict.Latest)} is out, this is {VersionPolicy.Format(Running)}";
                MelonLogger.Msg($"[Version] Drova Coop {VersionPolicy.Format(verdict.Latest)} is out; this is {VersionPolicy.Format(Running)}. {ReleasesUrl}");
                return;
            }

            _summary = "allowed, current";
            MelonLogger.Msg($"[Version] Build {VersionPolicy.Format(Running)} is current.");
        }

        private static Version ParseRunning()
        {
            // The generated constant is what MelonInfo reports, so the two cannot disagree. A constant
            // that does not parse is a build error somewhere else; treating it as 0.0.0 here would let
            // a minimum refuse it, which is the safer of the two mistakes.
            return VersionPolicy.TryParseVersion(BuildInfo.Version, out Version version) ? version : new Version(0, 0, 0, 0);
        }
    }
}
