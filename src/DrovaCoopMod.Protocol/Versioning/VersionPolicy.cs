namespace DrovaCoopMod.Versioning
{
    /// <summary>
    /// Which builds of the mod may host or join, as read from the <c>VERSION</c> file in the public
    /// repository.
    ///
    /// A build that corrupts saves cannot be recalled once it is on players' machines. What can be
    /// changed after the fact is one plaintext file, fetched at start, that names the builds not to run.
    /// The file is line based, one directive per line, with <c>#</c> starting a comment:
    /// <list type="bullet">
    /// <item><c>latest 0.1.31</c> names the newest release. Older builds are told, not refused.</item>
    /// <item><c>min 0.1.30 [reason]</c> refuses every build older than that.</item>
    /// </list>
    /// A line that does not parse is kept in <see cref="Ignored"/> and changes nothing, so a typo in the
    /// file can never lock everyone out. Absence of the file is not this type's concern: the fetch
    /// decides what an unreachable policy means, and it means allowed.
    /// </summary>
    public sealed class VersionPolicy
    {
        private static readonly char[] VersionEnd = { '-', '+', ' ' };

        private readonly List<string> _ignored;

        /// <summary>The newest release, or null when the file does not say.</summary>
        public Version? Latest { get; }

        /// <summary>The oldest build still allowed, or null when every build is.</summary>
        public Version? Minimum { get; }

        /// <summary>Why builds below <see cref="Minimum"/> are refused. Empty when the file gave none.</summary>
        public string MinimumReason { get; }

        /// <summary>Lines that did not parse, verbatim, so a log can say what the file got wrong.</summary>
        public IReadOnlyList<string> Ignored => _ignored;

        /// <summary>A policy that allows everything and knows of no release.</summary>
        public static VersionPolicy Empty { get; } = new(null, null, string.Empty, new List<string>());

        private VersionPolicy(Version? latest, Version? minimum, string minimumReason, List<string> ignored)
        {
            Latest = latest;
            Minimum = minimum;
            MinimumReason = minimumReason;
            _ignored = ignored;
        }

        /// <summary>
        /// Reads a policy file. Never throws: what cannot be read is ignored and listed.
        /// </summary>
        /// <param name="text">The file's text, or null for no file.</param>
        public static VersionPolicy Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Empty;

            Version? latest = null;
            Version? minimum = null;
            string minimumReason = string.Empty;
            List<string> ignored = new();

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();

                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment).Trim();
                if (line.Length == 0) continue;

                string[] parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    ignored.Add(line);
                    continue;
                }

                string reason = parts.Length > 2 ? parts[2].Trim() : string.Empty;

                switch (parts[0].ToLowerInvariant())
                {
                    case "latest":
                        if (TryParseVersion(parts[1], out Version parsedLatest)) latest = parsedLatest;
                        else ignored.Add(line);
                        break;

                    case "min":
                        if (TryParseVersion(parts[1], out Version parsedMinimum))
                        {
                            minimum = parsedMinimum;
                            minimumReason = reason;
                        }
                        else
                        {
                            ignored.Add(line);
                        }
                        break;

                    default:
                        ignored.Add(line);
                        break;
                }
            }

            return new VersionPolicy(latest, minimum, minimumReason, ignored);
        }

        /// <summary>
        /// Decides whether a build may run a session.
        /// </summary>
        /// <param name="running">The build asking.</param>
        public VersionVerdict Judge(Version running)
        {
            Version version = Normalize(running);
            bool newer = Latest != null && version < Latest;

            if (Minimum != null && version < Minimum)
            {
                string why = MinimumReason.Length > 0 ? MinimumReason : "builds before " + Format(Minimum) + " are no longer allowed";
                return new VersionVerdict(false, $"build {Format(version)} is not allowed to host or join: {why}.", Latest, newer);
            }

            return new VersionVerdict(true, null, Latest, newer);
        }

        /// <summary>
        /// Reads a version the way the file and the build write it: one to four numbers, an optional
        /// leading <c>v</c>, and anything after a dash or plus dropped, so <c>v0.1.30-beta</c> is
        /// <c>0.1.30</c>. Always four components, so that <c>0.1.30</c> and <c>0.1.30.0</c> compare equal.
        /// </summary>
        public static bool TryParseVersion(string? text, out Version version)
        {
            version = null!;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string trimmed = text.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(1);

            int cut = trimmed.IndexOfAny(VersionEnd);
            if (cut >= 0) trimmed = trimmed.Substring(0, cut);

            if (!trimmed.Contains('.')) trimmed += ".0";
            if (!Version.TryParse(trimmed, out Version? parsed) || parsed == null) return false;

            version = Normalize(parsed);
            return true;
        }

        /// <summary>
        /// The short form a player recognizes: the fourth component dropped when it is zero, so a
        /// normalized <c>0.1.30.0</c> reads as <c>0.1.30</c>.
        /// </summary>
        public static string Format(Version version)
        {
            Version normalized = Normalize(version);
            return normalized.Revision == 0 ? normalized.ToString(3) : normalized.ToString(4);
        }

        private static Version Normalize(Version version)
        {
            return new Version(
                Math.Max(0, version.Major),
                Math.Max(0, version.Minor),
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision));
        }
    }

    /// <summary>
    /// What a <see cref="VersionPolicy"/> made of one build.
    /// </summary>
    /// <param name="Allowed">Whether the build may host or join.</param>
    /// <param name="Refusal">The sentence to show when it may not. Null when allowed.</param>
    /// <param name="Latest">The newest release the policy knows of, or null.</param>
    /// <param name="NewerAvailable">Whether <paramref name="Latest"/> is newer than the build asking.</param>
    public readonly record struct VersionVerdict(bool Allowed, string? Refusal, Version? Latest, bool NewerAvailable);
}
