namespace DrovaCoopMod.World
{
    /// <summary>
    /// Rate-limits a subsystem's log lines per key rather than globally, so one noisy object cannot
    /// hide the one line that matters. One instance per subsystem, so their keys never collide.
    /// </summary>
    internal sealed class Complaints
    {
        /// <summary>How often one key may name itself in the log, in seconds.</summary>
        private const float QuietSeconds = 5f;

        private readonly Dictionary<string, float> _last = new(StringComparer.Ordinal);

        /// <summary>
        /// Whether a line about this key may be logged now. Saying yes starts its quiet period.
        /// </summary>
        internal bool MayComplainAbout(string key)
        {
            float now = UnityEngine.Time.unscaledTime;

            if (_last.TryGetValue(key, out float last) && now - last < QuietSeconds) return false;

            _last[key] = now;
            return true;
        }

        /// <summary>Forgets every quiet period. Called when a session ends.</summary>
        internal void Clear()
        {
            _last.Clear();
        }

        /// <summary>A save key cut to its first eight characters, enough to tell two apart in a log line.</summary>
        internal static string Short(string key)
        {
            return string.IsNullOrEmpty(key) || key.Length <= 8 ? key : key[..8];
        }
    }
}
