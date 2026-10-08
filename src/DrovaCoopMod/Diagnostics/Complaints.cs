namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// Per-key log rate limit, so one noisy object cannot hide the line that matters.
    /// </summary>
    internal sealed class Complaints
    {
        private const float QuietSeconds = 5f;

        private readonly Dictionary<string, float> _last = new(StringComparer.Ordinal);

        /// <summary>Whether a line about this key may be logged now. Yes starts its quiet period.</summary>
        internal bool MayComplainAbout(string key)
        {
            float now = UnityEngine.Time.unscaledTime;

            if (_last.TryGetValue(key, out float last) && now - last < QuietSeconds) return false;

            _last[key] = now;
            return true;
        }

        internal void Clear()
        {
            _last.Clear();
        }

        /// <summary>A save key cut to its first eight characters for log lines.</summary>
        internal static string Short(string key)
        {
            return string.IsNullOrEmpty(key) || key.Length <= 8 ? key : key[..8];
        }
    }
}
