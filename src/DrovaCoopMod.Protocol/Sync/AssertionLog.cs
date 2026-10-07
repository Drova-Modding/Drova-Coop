using DrovaCoopMod.Messages;

namespace DrovaCoopMod.Sync
{
    /// <summary>
    /// Puts incoming variable assertions back into the order the sending world wrote them in.
    /// <c>SetValue</c> runs its listener cascade inside itself, so causes arrive after their effects. An arrival opens a short
    /// settling window, and everything held is released in stamp order when it closes.
    /// </summary>
    public sealed class AssertionLog
    {
        /// <summary>How long arrivals are collected before release. Outlasts a one-frame sender cascade.</summary>
        public const float DefaultSettleSeconds = 0.05f;

        /// <summary>The most assertions held per sender inside one window.</summary>
        public const int MaxHeldPerOrigin = 512;

        private sealed class OriginState
        {
            internal readonly SortedDictionary<uint, GVarChangeMessage> Held = new();

            // Per variable, not per sender: stamps arrive descending, so a per-sender floor drops every late cause.
            internal readonly Dictionary<string, uint> Released = new(StringComparer.Ordinal);

            internal float Waiting;
        }

        private readonly Dictionary<byte, OriginState> _origins = new();
        private readonly Queue<GVarChangeMessage> _ready = new();
        private readonly float _settle;

        private int _skipped;

        /// <summary>Creates a log with the default settling window.</summary>
        public AssertionLog()
            : this(DefaultSettleSeconds)
        {
        }

        /// <summary>Creates a log with an explicit settling window.</summary>
        public AssertionLog(float settleSeconds)
        {
            _settle = settleSeconds;
        }

        /// <summary>How many assertions are collected and not yet released.</summary>
        public int Pending
        {
            get
            {
                int total = 0;

                foreach (OriginState state in _origins.Values)
                {
                    total += state.Held.Count;
                }

                return total;
            }
        }

        /// <summary>How many assertions were dropped because their variable already had a newer value.</summary>
        public int Skipped => _skipped;

        /// <summary>Forgets everything. Called when a session ends.</summary>
        public void Reset()
        {
            _origins.Clear();
            _ready.Clear();
            _skipped = 0;
        }

        /// <summary>Forgets one sender's history when they leave, since the id is reused and restamps from one.</summary>
        public void Forget(byte origin)
        {
            if (!_origins.TryGetValue(origin, out OriginState state)) return;

            Release(state);
            _origins.Remove(origin);
        }

        /// <summary>Accepts one assertion.</summary>
        public void Offer(in GVarChangeMessage message)
        {
            // Stamp zero is unnumbered, such as a catch-up record, so it skips the window.
            if (message.Sequence == 0)
            {
                _ready.Enqueue(message);
                return;
            }

            if (!_origins.TryGetValue(message.Origin, out OriginState state))
            {
                state = new OriginState();
                _origins[message.Origin] = state;
            }

            // Refused only when this same variable already has a newer value.
            if (state.Released.TryGetValue(Key(message), out uint released)
                && message.Sequence <= released)
            {
                _skipped++;
                return;
            }

            // Full: release early rather than drop, since a burst's tail matters most.
            if (state.Held.Count >= MaxHeldPerOrigin) Release(state);

            state.Held[message.Sequence] = message;
        }

        /// <summary>Lets time pass, which closes settling windows.</summary>
        public void Tick(float deltaSeconds)
        {
            foreach (OriginState state in _origins.Values)
            {
                if (state.Held.Count == 0)
                {
                    state.Waiting = 0f;
                    continue;
                }

                state.Waiting += deltaSeconds;

                if (state.Waiting < _settle) continue;

                Release(state);
            }
        }

        /// <summary>Takes the next assertion that is ready to be applied.</summary>
        public bool TryTake(out GVarChangeMessage message)
        {
            if (_ready.Count == 0)
            {
                message = default;
                return false;
            }

            message = _ready.Dequeue();
            return true;
        }

        private static string Key(in GVarChangeMessage message)
        {
            return (message.ListGuid ?? string.Empty) + "\n" + (message.VarId ?? string.Empty);
        }

        private void Release(OriginState state)
        {
            foreach (KeyValuePair<uint, GVarChangeMessage> pair in state.Held)
            {
                state.Released[Key(pair.Value)] = pair.Key;

                _ready.Enqueue(pair.Value);
            }

            state.Held.Clear();
            state.Waiting = 0f;
        }
    }
}
