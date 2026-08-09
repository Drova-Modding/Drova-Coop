using DrovaCoopMod.Messages;

namespace DrovaCoopMod.Sync
{
    /// <summary>
    /// Puts incoming variable assertions back into the order the sending world wrote them in.
    ///
    /// The problem it solves is not the network's - delivery is reliable and ordered. It is the write
    /// hook's. <c>AGVar&lt;T&gt;.SetValue</c> invokes its whole listener cascade from inside itself, so
    /// a listener that writes a second variable finishes - postfix included - before the outer postfix
    /// runs. A hook that reports from the postfix therefore puts every effect on the wire in front of
    /// its own cause, and the receiving world runs a story backwards. The sender stamps each write in
    /// the prefix instead, before anything it causes can happen, and this class hands them out in
    /// stamp order.
    ///
    /// The consequence of that inversion is what shapes the design, and it is not obvious: a lower
    /// stamp always arrives <em>later</em>, because the cause is sent last. So an arriving stamp can
    /// never be assumed to be the earliest one still coming, and a log that released each message as
    /// soon as the one before it was accounted for would release every effect before its cause - which
    /// is exactly the bug it was written to fix. Instead an arrival opens a short settling window; when
    /// that window closes, everything held is released at once, in ascending stamp order. The window
    /// only has to outlast one sender-side cascade, which completes inside a single frame.
    ///
    /// Deliberately game-free, and deliberately in the protocol assembly. What has to be right here -
    /// that an effect never overtakes its cause, that a straggler cannot undo a newer value, that
    /// waiting always ends - is decidable from numbers alone, and a rule that can only be exercised by
    /// playing a quest for twenty minutes is a rule nobody exercises.
    /// </summary>
    public sealed class AssertionLog
    {
        /// <summary>
        /// How long arrivals are collected before they are released in order.
        ///
        /// Long enough to outlast a sender-side listener cascade, which happens within one frame, and
        /// short enough to disappear into the round trip: the session's own measured floor is 60-120 ms
        /// of pump latency, so this is not what anybody will feel.
        /// </summary>
        public const float DefaultSettleSeconds = 0.05f;

        /// <summary>
        /// The most assertions held per sender inside one window. Reached only under a flood, and a
        /// bound is preferable to an unbounded buffer fed by the network.
        /// </summary>
        public const int MaxHeldPerOrigin = 512;

        private sealed class OriginState
        {
            internal readonly SortedDictionary<uint, GVarChangeMessage> Held = new();

            /// <summary>
            /// The newest stamp already released for each variable this sender has spoken about.
            ///
            /// Per variable, deliberately, and this was measured rather than reasoned: a single
            /// per-sender floor looks equivalent and is catastrophically not. A deep cascade is sent
            /// innermost-first, so stamps arrive descending; when a settling window closes partway
            /// through a burst, every message still to come is below a per-sender floor and is thrown
            /// away as a straggler. One live session lost 189 of a peer's 363 assertions that way.
            /// The floor only ever existed to stop an older value overwriting a newer one, and that
            /// is a question about one variable, not about the sender.
            /// </summary>
            internal readonly Dictionary<string, uint> Released = new(StringComparer.Ordinal);

            internal float Waiting;
        }

        private readonly Dictionary<byte, OriginState> _origins = new();
        private readonly Queue<GVarChangeMessage> _ready = new();
        private readonly float _settle;

        private int _skipped;
        private int _reordered;
        private int _dropped;

        /// <summary>Creates a log with the default settling window.</summary>
        public AssertionLog()
            : this(DefaultSettleSeconds)
        {
        }

        /// <summary>Creates a log with an explicit settling window, which is what the tests vary.</summary>
        /// <param name="settleSeconds">How long arrivals are collected before being released.</param>
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

        /// <summary>
        /// How many assertions arrived after their neighbours had already been released - a straggler
        /// from outside its settling window. Every one of them is a value this world did not take.
        /// </summary>
        public int Skipped => _skipped;

        /// <summary>How many had to be put back in front of something. Healthy, and expected.</summary>
        public int Reordered => _reordered;

        /// <summary>How many were refused because a sender's window hit its bound.</summary>
        public int Dropped => _dropped;

        /// <summary>Forgets everything. Called when a session ends.</summary>
        public void Reset()
        {
            _origins.Clear();
            _ready.Clear();
            _skipped = 0;
            _reordered = 0;
            _dropped = 0;
        }

        /// <summary>
        /// Accepts one assertion.
        /// </summary>
        /// <param name="message">The assertion, as it arrived.</param>
        public void Offer(in GVarChangeMessage message)
        {
            // Stamp zero means the sender does not number its writes - an older peer, or a catch-up
            // record, which is a value rather than a moment. Nothing to order it against, so it goes
            // straight through rather than waiting out a window it is not part of.
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

            // Refused only when this same variable has already been given a newer value. A stamp
            // lower than one released for some OTHER variable is an ordinary late cause, and dropping
            // it would silently lose it.
            if (state.Released.TryGetValue(Key(message), out uint released)
                && message.Sequence <= released)
            {
                _skipped++;
                return;
            }

            if (state.Held.Count >= MaxHeldPerOrigin)
            {
                _dropped++;
                return;
            }

            if (state.Held.Count > 0 && message.Sequence < LowestHeld(state))
            {
                // The cause of something already in this window. Exactly the case the settling window
                // exists for, and worth counting so a log can say how often it happens.
                _reordered++;
            }

            state.Held[message.Sequence] = message;
        }

        /// <summary>
        /// Lets time pass, which is what closes a settling window.
        /// </summary>
        /// <param name="deltaSeconds">Seconds since the last call.</param>
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

        /// <summary>
        /// Takes the next assertion that is ready to be applied.
        /// </summary>
        /// <param name="message">The assertion.</param>
        /// <returns>False when nothing is ready.</returns>
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

        private static uint LowestHeld(OriginState state)
        {
            foreach (uint sequence in state.Held.Keys)
            {
                return sequence;
            }

            return uint.MaxValue;
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
