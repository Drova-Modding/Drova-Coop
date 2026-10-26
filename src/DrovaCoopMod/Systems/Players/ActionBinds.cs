using Drova_Modding_API.Extensions;
using Il2CppDrova;
using Il2CppDrova.ActorActions;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Turns what an actor is doing into its <c>EActionBind</c> byte, and back. Polled rather than
    /// patched, because <c>TryStartAction</c> is inlined in this build and a patch on it never fires.
    /// </summary>
    internal static class ActionBinds
    {
        /// <summary>Doing nothing. Relies on <c>EActionBind.None</c> being zero.</summary>
        internal static readonly Snapshot Idle = default;

        /// <summary>
        /// Every action a player can bind. Listed by hand so a value the game adds later is ignored.
        /// </summary>
        private static readonly Actor.ActionModule.EActionBind[] Known =
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
        /// One use of one action. The counters tell combo swings apart, since the bind stays
        /// <c>PrimaryAttack</c> throughout a combo.
        /// </summary>
        internal readonly struct Snapshot(Actor.ActionModule.EActionBind bind, int usage, int combo, IntPtr instance, bool released)
        {
            /// <summary>
            /// Whether the player has let the button go, which happens long before a tapped swing ends.
            /// </summary>
            internal bool Released { get; } = released;

            /// <summary>Which action, or <c>None</c> for nothing bound.</summary>
            internal Actor.ActionModule.EActionBind Bind { get; } = bind;

            /// <summary>How many times this action instance has been started.</summary>
            internal int Usage { get; } = usage;

            /// <summary>How far into a combo this use is.</summary>
            internal int Combo { get; } = combo;

            /// <summary>
            /// The running action object's native address. Never sent, only compared locally to catch a
            /// combo's switch between objects.
            /// </summary>
            private IntPtr Instance { get; } = instance;

            /// <summary>
            /// Whether this is the same swing as <paramref name="other"/>. <see cref="Released"/> is
            /// excluded, because letting go ends a swing rather than starting a new one.
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
        /// What a player is doing right now. An action that matches no bind reads as idle.
        /// </summary>
        internal static Snapshot Current(Actor.ActionModule? module)
        {
            Snapshot snapshot = Observe(module, out _);

            return snapshot.Bind == Actor.ActionModule.EActionBind.None ? Idle : snapshot;
        }

        /// <summary>
        /// The same reading for a creature, whose AI-installed binds do not exist on the far side, so
        /// the swing's prefab is returned too and an unmatched swing keeps its counters.
        /// </summary>
        /// <param name="module">The creature's action module.</param>
        /// <param name="rootPrefab">The combo chain's root prefab, stable while the chain plays. Null when idle.</param>
        internal static Snapshot Observe(Actor.ActionModule? module, out AA_ABase? rootPrefab)
        {
            rootPrefab = null;

            if (module == null || !module.HasCurrentAction) return default;

            AA_ABase? current = module.CurrentAction;
            if (current == null) return default;

            // A combo's later swings are child instances built by AA_Atk_Combo that match no bind.
            // Their root carries the bound prefab, and roots are flattened, so one hop is enough.
            AA_ABase? root = current.RootActionInstance;

            rootPrefab = root != null ? root.Prefab : current.Prefab;

            // Only phasic actions can be released. Anything else counts as held.
            AA_APhasicAction? phasic = current.PooledCast<AA_APhasicAction>();
            bool released = phasic != null && phasic._releaseRequested;

            foreach (Actor.ActionModule.EActionBind bind in Known)
            {
                AA_ABase? bound = module.GetBindedPrefabAction(bind);
                if (bound == null) continue;

                bool matches = Same(bound, current.Prefab)
                    || Same(bound, current)
                    || (root != null && (Same(bound, root.Prefab) || Same(bound, root)));

                if (matches)
                {
                    return new Snapshot(bind, current.UsageCounter, current.ComboCount, current.Pointer, released);
                }
            }

            return new Snapshot(Actor.ActionModule.EActionBind.None,
                current.UsageCounter, current.ComboCount, current.Pointer, released);
        }

        /// <summary>
        /// Reads a bind off the wire, refusing values this build does not know.
        /// </summary>
        internal static bool TryFromWire(byte value, out Actor.ActionModule.EActionBind bind)
        {
            foreach (Actor.ActionModule.EActionBind known in Known)
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
