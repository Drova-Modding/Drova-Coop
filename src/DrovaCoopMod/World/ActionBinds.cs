using Il2CppDrova;
using Il2CppDrova.ActorActions;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Turns what an actor is doing into the one byte that says so, and back.
    ///
    /// Drova binds each of a player's inputs - attack, dodge, drink - to an action prefab, and starting
    /// one is <c>TryStartAction(EActionBind)</c>. That is the vocabulary worth replicating: it is small,
    /// it is stable across builds in a way an action instance is not, and it is exactly what the local
    /// input layer says, so the far end can say it to its own copy of the game and get the animation, the
    /// timing and the stamina cost without any of them being described on the wire.
    ///
    /// Read by polling rather than by patching <c>TryStartAction</c>. A patch would be the obvious route
    /// and is the wrong one here: small methods in this build are inlined into their callers, and a patch
    /// on one of those applies, reports success and never runs - twice already in this mod. Polling cannot
    /// fail that way. What it costs is that an action beginning and ending inside one send interval is
    /// never seen; at 30 Hz that is 33 ms, and nothing a player can do in this game is that brief.
    /// </summary>
    internal static class ActionBinds
    {
        /// <summary>
        /// The default <see cref="Snapshot"/> means "doing nothing", which relies on
        /// <c>EActionBind.None</c> being zero. It is, and this is the one place that depends on it.
        /// </summary>
        internal static readonly Snapshot Idle = default;

        /// <summary>
        /// Every action a player can bind, and nothing else. Enumerated by hand rather than swept from
        /// the enum, so a value the game adds later is ignored until somebody has decided what it means
        /// rather than being replicated on the strength of existing.
        /// </summary>
        private static readonly Actor.ActionModule.EActionBind[] _known =
        [
            Actor.ActionModule.EActionBind.PrimaryAttack,
            Actor.ActionModule.EActionBind.SecondaryAttack,
            Actor.ActionModule.EActionBind.Dodge,
            Actor.ActionModule.EActionBind.InteractGeneric,
            Actor.ActionModule.EActionBind.InteractDrink,
            Actor.ActionModule.EActionBind.InteractEat,
            Actor.ActionModule.EActionBind.InteractPickUp,
        ];

        /// <summary>
        /// One use of one action: which action, and enough about it to tell this use from the next.
        ///
        /// The bind alone is not enough, and a combo is why. Attack, attack again, and the second swing is
        /// the same <c>PrimaryAttack</c> continuing - the action never ends and never returns to
        /// <c>None</c> in between - so anything watching only the bind sees one unbroken attack and
        /// replicates the first swing of every combo and none of the rest. The counters are what make the
        /// second swing visible: the game raises the combo count for a continuation and the usage count for
        /// a fresh start, so between them, every distinct swing changes something.
        /// </summary>
        internal readonly struct Snapshot(Actor.ActionModule.EActionBind bind, int usage, int combo, IntPtr instance, bool released)
        {
            /// <summary>
            /// Whether the player has let the button go.
            ///
            /// This is not the same as the action being over, and conflating the two is what made
            /// every ordinary attack replicate as a charged one. A tap starts a swing and releases
            /// immediately; the swing then runs on for half a second by itself. A body told only when the
            /// action *ends* holds the button for all of it, which is the definition of charging it.
            /// </summary>
            internal bool Released { get; } = released;

            /// <summary>Which action, or <c>None</c> for nothing bound.</summary>
            internal Actor.ActionModule.EActionBind Bind { get; } = bind;

            /// <summary>How many times this action instance has been started.</summary>
            internal int Usage { get; } = usage;

            /// <summary>How far into a combo this use is.</summary>
            internal int Combo { get; } = combo;

            /// <summary>
            /// Which action object is running, as its native address. Never sent - a pointer means nothing
            /// on another machine - and compared only against this machine's own previous sample.
            ///
            /// It is what makes a combo detectable at all. The counters can coincide across two different
            /// action objects, and a combo is precisely a switch between objects, so identity is the one
            /// signal that cannot miss it.
            /// </summary>
            internal IntPtr Instance { get; } = instance;

            /// <summary>
            /// Whether this describes the same swing as <paramref name="other"/> rather than a new one.
            ///
            /// <see cref="Released"/> is deliberately not part of the comparison, however much it looks
            /// like state that belongs here. Letting go does not begin a second swing, it ends the first,
            /// so counting it as a new use makes the receiver start the action again: every tap replicated
            /// as two swings, and a charged attack as two because holding and letting go are two samples
            /// of the one use. It also cost the combo, since a body busy replaying a duplicate is past the
            /// window by the time the real second swing arrives.
            ///
            /// The release still has to go out promptly - a tap whose release is late reads as a charge -
            /// but that is a question about when to send, not about what is being counted, and it is
            /// answered where the sending is paced.
            /// </summary>
            internal bool IsSameUse(in Snapshot other)
            {
                return Bind == other.Bind
                    && Usage == other.Usage
                    && Combo == other.Combo
                    && Instance == other.Instance;
            }
        }

        /// <summary>
        /// What an actor is doing right now.
        ///
        /// The action module hands out the running <em>instance</em>, and the binds name <em>prefabs</em>,
        /// so the two are matched through the instance's prefab. Instances are pooled and reused, which is
        /// why the instance itself is compared too: an action taken from the pool can be the very object a
        /// bind points at.
        /// </summary>
        internal static Snapshot Current(Actor.ActionModule? module)
        {
            if (module == null || !module.HasCurrentAction) return default;

            AA_ABase current = module.CurrentAction;
            if (current == null) return default;

            // The root as well as the action itself, because a combo is a different action object.
            // The second swing of a combo is not the bound attack continuing - it is a child instance
            // built from the combo's own prefab by AA_Atk_Combo, so it matches no bind at all and, without
            // this, reads as the player having stopped attacking. Its root is the attack that began the
            // chain, and that one does carry the bound prefab. Roots are flattened when they are made, so
            // one hop is always enough however long the chain gets.
            AA_ABase root = current.RootActionInstance;

            foreach (Actor.ActionModule.EActionBind bind in _known)
            {
                AA_ABase bound = module.GetBindedPrefabAction(bind);
                if (bound == null) continue;

                // Compared by pointer, not with the equality operator: these are proxies over native
                // objects, and two proxies for the same object are different managed instances. Unity's
                // own operator would only make that worse, since it answers a question about destruction
                // rather than about identity.
                bool matches = Same(bound, current.Prefab)
                    || Same(bound, current)
                    || (root != null && (Same(bound, root.Prefab) || Same(bound, root)));

                if (matches)
                {
                    // Only phasic actions have a release to request; anything else is treated as still
                    // held, which is what a body does with an action that cannot be let go of.
                    AA_APhasicAction? phasic = current.TryCast<AA_APhasicAction>();
                    bool released = phasic != null && phasic._releaseRequested;

                    return new Snapshot(bind, current.UsageCounter, current.ComboCount, current.Pointer, released);
                }
            }

            return default;
        }

        /// <summary>
        /// Reads a bind off the wire, refusing anything this build does not know.
        ///
        /// The numbering is the game's, so a peer on another version can name an action that does not
        /// exist here. Casting that straight through would hand the action module a value it will do
        /// something undefined with, on a body the player cannot see the inside of.
        /// </summary>
        internal static bool TryFromWire(byte value, out Actor.ActionModule.EActionBind bind)
        {
            foreach (Actor.ActionModule.EActionBind known in _known)
            {
                if ((byte)known != value) continue;

                bind = known;
                return true;
            }

            bind = Actor.ActionModule.EActionBind.None;
            return value == (byte)Actor.ActionModule.EActionBind.None;
        }

        private static bool Same(AA_ABase left, AA_ABase? right)
        {
            return right != null && left.Pointer == right.Pointer;
        }
    }
}
