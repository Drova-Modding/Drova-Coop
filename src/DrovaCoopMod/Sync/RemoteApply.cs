namespace DrovaCoopMod.Sync
{
    /// <summary>
    /// Marks a subsystem that replicates state, so <see cref="RemoteApply{TScope}"/> can keep one
    /// independent guard per subsystem rather than a single global one.
    ///
    /// Separate scopes matter: applying a replicated health change legitimately touches the inventory
    /// (a consumed potion), and a single shared flag would swallow that inventory change as if it were
    /// an echo of the health one.
    /// </summary>
    internal interface IApplyScope
    {
    }

    /// <summary>Guards replicated health writes.</summary>
    internal readonly struct HealthScope : IApplyScope
    {
    }

    /// <summary>Guards replicated inventory writes.</summary>
    internal readonly struct InventoryScope : IApplyScope
    {
    }

    /// <summary>Guards replicated global-variable and quest-state writes.</summary>
    internal readonly struct GVarScope : IApplyScope
    {
    }

    /// <summary>
    /// Stops replicated changes from being re-broadcast as if the local game had made them.
    ///
    /// Applying a change that arrived over the wire means calling the game's own code, which re-enters
    /// the very method the sync hook watches. Without a guard the hook sees its own write, sends it,
    /// the other side applies it, its hook fires, and the two peers ping-pong a single event forever.
    /// Every surveyed project hit this; Skyrim Together's answer - refcounted thread-local scope guards,
    /// one per subsystem - is the one being copied here.
    ///
    /// Use it as a matched pair. Around a remote apply:
    /// <code>
    /// using (RemoteApply&lt;HealthScope&gt;.Enter())
    /// {
    ///     health.ChangeHealth(change);
    /// }
    /// </code>
    /// and as the first line of the hook that would otherwise re-broadcast it:
    /// <code>
    /// if (RemoteApply&lt;HealthScope&gt;.InProgress) return;
    /// </code>
    ///
    /// A counter rather than a flag, because applying one remote change can legitimately cause another
    /// in the same subsystem; a bool would be cleared by the inner scope while the outer one is still
    /// running, and the outer change would escape.
    /// </summary>
    /// <typeparam name="TScope">Which subsystem is being guarded. Each closed generic gets its own
    /// counter, which is the whole reason this is generic.</typeparam>
    internal static class RemoteApply<TScope> where TScope : struct, IApplyScope
    {
        // Thread-static because the count is only meaningful on the thread doing the applying. Everything
        // should be on the Unity main thread, but "should" is doing real work in that sentence: a guard
        // that leaks across threads fails by suppressing a genuine local change, which is a desync that
        // looks like a dropped packet.
        [ThreadStatic]
        private static int _depth;

        /// <summary>
        /// Whether this thread is currently applying a replicated change to this subsystem. Hooks that
        /// broadcast must return early when it is true.
        /// </summary>
        internal static bool InProgress => _depth > 0;

        /// <summary>
        /// Opens a scope. Dispose it - via <c>using</c> - to close it.
        /// </summary>
        internal static Scope Enter()
        {
            _depth++;
            return default;
        }

        /// <summary>
        /// The open scope. A struct so entering allocates nothing, which matters because this sits on the
        /// path of every replicated change.
        /// </summary>
        internal readonly struct Scope : IDisposable
        {
            /// <summary>
            /// Closes the scope.
            /// </summary>
            public void Dispose()
            {
                // Floored rather than trusted. A stray Dispose would otherwise drive the count negative
                // and disable the guard permanently, turning a paired-call bug into an infinite
                // re-broadcast much later and somewhere else.
                if (_depth > 0)
                {
                    _depth--;
                }
            }
        }
    }
}
