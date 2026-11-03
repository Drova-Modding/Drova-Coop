namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// Per-key log rate limit, so one noisy object cannot hide the line that matters.
    /// </summary>
    internal sealed class Complaints
    {
        private const float QuietSeconds = 5f;

        /// <summary>Keys often come from a peer, which could otherwise send a new one with every message.</summary>
        private const int MaxKeys = 256;
        private const int MaxLinesPerSecond = 10;

        private readonly Dictionary<string, float> _last = new(StringComparer.Ordinal);
        private int _second;
        private int _linesThisSecond;

        /// <summary>Whether a line about this key may be logged now. Yes starts its quiet period.</summary>
        internal bool MayComplainAbout(string key)
        {
            float now = UnityEngine.Time.unscaledTime;

            if (_last.TryGetValue(key, out float last) && now - last < QuietSeconds) return false;

            if ((int)now != _second)
            {
                _second = (int)now;
                _linesThisSecond = 0;
            }

            if (_linesThisSecond >= MaxLinesPerSecond) return false;

            if (_last.Count >= MaxKeys) _last.Clear();

            _linesThisSecond++;
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
