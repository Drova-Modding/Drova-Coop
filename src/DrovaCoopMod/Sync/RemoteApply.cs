namespace DrovaCoopMod.Sync
{
    /// <summary>
    /// Marks a subsystem that replicates state, giving each its own <see cref="RemoteApply{TScope}"/> guard.
    /// A shared guard would swallow a legitimate change in one subsystem caused by applying another.
    /// </summary>
    internal interface IApplyScope;

    /// <summary>
    /// Stops replicated changes from being re-broadcast as local ones. Wrap the apply in
    /// <c>using (RemoteApply&lt;X&gt;.Enter())</c> and return early from the broadcasting hook while
    /// <see cref="InProgress"/>. A counter, so nested applies do not clear the outer guard.
    /// </summary>
    /// <typeparam name="TScope">The guarded subsystem. Each closed generic gets its own counter.</typeparam>
    internal static class RemoteApply<TScope> where TScope : struct, IApplyScope
    {
        // Thread-static so a guard can never suppress a genuine change on another thread.
        [ThreadStatic]
        // ReSharper disable once StaticMemberInGenericType we want this to be per closed generic, not per type.
        private static int _depth;

        /// <summary>Whether this thread is applying a replicated change to this subsystem.</summary>
        internal static bool InProgress => _depth > 0;

        /// <summary>Opens a scope. Dispose it to close it.</summary>
        internal static Scope Enter()
        {
            _depth++;
            return default;
        }

        /// <summary>The open scope.</summary>
        internal readonly struct Scope : IDisposable
        {
            /// <summary>Closes the scope.</summary>
            public void Dispose()
            {
                // Floored, so a stray Dispose cannot disable the guard for good.
                if (_depth > 0)
                {
                    _depth--;
                }
            }
        }
    }
}
