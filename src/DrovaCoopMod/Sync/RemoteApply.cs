namespace DrovaCoopMod.Sync
{
    /// <summary>
    /// Marks a subsystem that replicates state, giving each its own <see cref="RemoteApply{TScope}"/> guard.
    /// A shared guard would swallow a legitimate change in one subsystem caused by applying another.
    /// </summary>
    internal interface IApplyScope
    {
    }

    /// <summary>Guards replicated health writes.</summary>
    internal readonly struct HealthScope : IApplyScope
    {
    }

    /// <summary>Guards replicated global-variable and quest-state writes.</summary>
    internal readonly struct GVarScope : IApplyScope
    {
    }

    /// <summary>Guards replicated cutscene playback.</summary>
    internal readonly struct CutsceneScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards replicated work at a resource spot. Separate from <see cref="GVarScope"/> because a replayed
    /// swing legitimately writes gvars such as the rubble counter.
    /// </summary>
    internal readonly struct WorkScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards a replicated haul. Not <see cref="WorkScope"/>, because <c>GVarDerived</c> would then mark the
    /// haul's cascade as derived on the far machine only.
    /// </summary>
    internal readonly struct HaulScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards a host world-time write, which <c>World.ClockGuard</c> would otherwise refuse on a client.
    /// </summary>
    internal readonly struct DaytimeScope : IApplyScope
    {
    }

    /// <summary>Guards replicated journal entries.</summary>
    internal readonly struct JournalScope : IApplyScope
    {
    }

    /// <summary>Guards replicated ability casts.</summary>
    internal readonly struct ItemUseScope : IApplyScope
    {
    }

    /// <summary>Guards replicated recipes and journal entries.</summary>
    internal readonly struct KnowledgeScope : IApplyScope
    {
    }

    /// <summary>Guards replicated status effects.</summary>
    internal readonly struct StatusScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards replicated health writes to non-players. Separate from <see cref="HealthScope"/> because an
    /// enemy's death can legitimately damage players.
    /// </summary>
    internal readonly struct EnemyHealthScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards a relayed blow dealt through the game's damage path. Unlike a replicated death, the death a
    /// blow causes here is this machine's own news to announce.
    /// </summary>
    internal readonly struct EnemyBlowScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards a shared take put into this world's container (<see cref="World.LootShare"/>), whose refill
    /// fires the take listener.
    /// </summary>
    internal readonly struct LootScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards a teammate's world-object state written here (<see cref="World.WorldObjects"/>), whose
    /// listener fires the same for loads and players.
    /// </summary>
    internal readonly struct WorldObjectScope : IApplyScope
    {
    }

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
