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

    /// <summary>Guards replicated cutscene playback.</summary>
    internal readonly struct CutsceneScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards replicated work at a resource spot.
    ///
    /// Its own scope rather than sharing <see cref="GVarScope"/>, for the reason this file already
    /// gives for splitting enemy health from player health: replaying a teammate's swing legitimately
    /// writes global variables - the quest's rubble counter is one - and a shared counter would
    /// swallow those as though they were echoes of the write that happened to be open.
    /// </summary>
    internal readonly struct WorkScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards a replicated haul: a rubble pile removed here to match a teammate, and the one unit of
    /// the quest tally that goes with it.
    ///
    /// Its own scope rather than reusing <see cref="WorkScope"/>, and the reason is not tidiness.
    /// <c>GVarDerived</c> marks any relative write that happens inside the work window as this world's
    /// own tally, permanently. A dump applied under the work scope would enrol the whole consequence
    /// cascade of the zero crossing - the observer that fires there and everything under it - into the
    /// derived set on the FAR machine only, because on the machine that actually dumped, that same
    /// cascade runs outside every window. Asymmetric derived sets mean one peer broadcasts totals for a
    /// variable the other refuses to accept.
    /// </summary>
    internal readonly struct HaulScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards a write of world time that came from the host.
    ///
    /// Load-bearing rather than decoration. <c>World.ClockGuard</c> refuses every non-saving clock
    /// write on a client - and the followed sleep in <c>World.DaytimeSync</c> IS a non-saving clock
    /// write, because turning the world over is exactly what it is for. Without this scope the guard
    /// eats the host's own turnover and clients silently stop following host sleeps, which is a worse
    /// bug than the one being fixed and far harder to see.
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
    /// Guards replicated writes to the health of something that is not a player.
    ///
    /// Separate from <see cref="HealthScope"/> on purpose, and the separation is load-bearing. Killing
    /// an enemy legitimately damages players: a death throe, a reflected blow, an explosion the corpse
    /// leaves behind. Those are real local events that the far player has to be told about, and sharing
    /// one health guard would swallow every one of them as though it were an echo of the enemy write that
    /// happened to be open at the time.
    /// </summary>
    internal readonly struct EnemyHealthScope : IApplyScope
    {
    }

    /// <summary>
    /// Guards a blow that arrived over the wire while it is dealt through the game's own damage path.
    ///
    /// Split out of <see cref="EnemyHealthScope"/> because the two mean opposite things to the death
    /// watch, and one scope carrying both meanings is how a creature came to die on one machine and be
    /// announced by nobody. A replicated <em>death</em> must not be re-announced: that really is this
    /// machine echoing what it was told. A replicated <em>blow</em> is a fight resolving here for the
    /// first time, and the death it causes is this machine's own news to tell - it is the only machine
    /// that ran the numbers.
    /// </summary>
    /// <summary>
    /// A shared take being put into this world's copy of a pickup or container. See
    /// <see cref="World.LootShare"/>. Its own scope, because refilling a chest fires the same inventory
    /// listener that reports a take, and that report must not go back out.
    /// </summary>
    internal readonly struct LootScope : IApplyScope
    {
    }

    internal readonly struct EnemyBlowScope : IApplyScope
    {
    }

    /// <summary>
    /// A teammate's world-object state being written into this world's copy. See
    /// <see cref="World.WorldObjects"/>.
    ///
    /// Its own scope, because the components that carry this state report a change through the same
    /// listener whether the change came from a player or from a load. Applying one without a guard is
    /// the textbook ping-pong: the far machine's listener fires, sends it back, and the pair trades one
    /// opened door forever.
    /// </summary>
    internal readonly struct WorldObjectScope : IApplyScope
    {
    }

    /// <summary>
    /// Stops replicated changes from being re-broadcast as if the local game had made them.
    ///
    /// Applying a change that arrived over the wire means calling the game's own code, which re-enters
    /// the very method the sync hook watches. Without a guard the hook sees its own write, sends it,
    /// the other side applies it, its hook fires, and the two peers ping-pong a single event forever -
    /// growing, because each round trip can produce more than one write.
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
