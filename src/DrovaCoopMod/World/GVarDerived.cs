using Drova_Modding_API;
using DrovaCoopMod.Sync;
using Il2CppDrova.GlobalVarSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Remembers which global variables this world computes for itself, so they are never copied
    /// from another one.
    ///
    /// A quest counter like the mine's rubble tally is not a fact about the world; it is a tally each
    /// machine keeps of things that happened in <em>its</em> world. Replicating it as an absolute
    /// number is wrong however carefully it is done: the two machines overwrite each other's totals
    /// and the count stops matching the piles actually lying there, which is exactly what the live
    /// session showed - the value walking 0,1,2,1,0,1 while both players mined.
    ///
    /// The discovery costs no wire traffic and needs no table. A variable written by a RELATIVE
    /// operation - add or subtract, never a plain set - during work this mod replicates is by
    /// definition a tally of that work. Both machines see the same operation inside the same window:
    /// the acting one during its own swing, the far one during the replay. So both mark it at the
    /// same instant, independently, and from then on each keeps its own count while the CAUSE
    /// continues to cross.
    ///
    /// Sticky for the session, because a variable that was ever derived stays derived; forgotten when
    /// the session ends or a savegame rewinds this world.
    /// </summary>
    internal static class GVarDerived
    {
        private static readonly HashSet<string> _derived = new(StringComparer.Ordinal);

        /// <summary>How many variables were found to be tallies. For <c>coop_status</c>.</summary>
        internal static int Count => _derived.Count;

        /// <summary>How many arriving absolutes were refused because of that. For <c>coop_status</c>.</summary>
        internal static int RefusedAbsolutes { get; private set; }

        /// <summary>
        /// How many relative writes were dropped because this machine was only mirroring a teammate's
        /// event. For <c>coop_status</c>.
        /// </summary>
        internal static int MirroredSkipped { get; private set; }

        /// <summary>Whether the discovery hook is in place.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>Every variable found to be a tally, as "List.Var". For <c>coop_gvar_derived</c>.</summary>
        internal static IEnumerable<string> Names => _derived;

        /// <summary>Forgets everything. Called when a session ends and after a savegame load.</summary>
        internal static void Reset()
        {
            _derived.Clear();
            RefusedAbsolutes = 0;
            MirroredSkipped = 0;
        }

        /// <summary>
        /// Applies the discovery hook. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooked = Hooking.TryPrefix(harmony, typeof(GInt), nameof(GInt.Operate),
                typeof(GVarDerived), nameof(OperatePrefix));

            Hooking.TryPrefix(harmony, typeof(GBool), nameof(GBool.Operate),
                typeof(GVarDerived), nameof(BoolOperatePrefix));

            if (!Hooked)
            {
                MelonLogger.Warning("[GVar] the derived-variable hook did not apply; quest tallies " +
                    "such as the mine's rubble count will be replicated as absolutes and will fight " +
                    "between the two worlds.");
            }
        }

        /// <summary>
        /// Whether a variable is this world's own tally.
        /// </summary>
        /// <param name="listGuid">The owning list's guid.</param>
        /// <param name="varId">The variable's id within that list.</param>
        internal static bool IsDerived(string listGuid, string varId)
        {
            return _derived.Count > 0 && _derived.Contains(Key(listGuid, varId));
        }

        /// <summary>Counts an arriving absolute that was refused because the variable is a tally.</summary>
        internal static void CountRefusal()
        {
            RefusedAbsolutes++;
        }

        /// <summary>
        /// Whether this machine is inside a cause this mod replicates, so that a relative write
        /// happening right now is a tally of that cause rather than an unrelated count.
        ///
        /// One predicate rather than the condition spelled out at each site, so that a new subsystem
        /// enrols in one place instead of quietly becoming a second source of double-counting.
        /// </summary>
        internal static bool IsInsideReplicatedCause
        {
            get => ResourceWork.IsLocalWorkInProgress || RemoteApply<WorkScope>.InProgress;
        }

        /// <summary>
        /// Whether this machine is only mirroring an event a teammate's world already resolved - a
        /// creature's death or health figure copied in, or a teammate's variable change replayed.
        ///
        /// The authored observers run on the mirror exactly as they ran where it happened, and a
        /// relative one - count a kill, step a boss phase, flip a flag - then acts twice: once there,
        /// whose absolute result crosses anyway, and once here on top of it. Roarke's boar count, the
        /// firefly standing and the Wanderwesen phase counter all doubled that way. The write is
        /// dropped on the mirror; the teammate's absolute is the answer.
        ///
        /// Not <see cref="EnemyBlowScope"/>: a relayed blow resolves the fight here for the first time.
        /// </summary>
        private static bool IsMirroring
        {
            get => RemoteApply<EnemyHealthScope>.InProgress || RemoteApply<StoryScope>.InProgress;
        }

        private static bool BoolOperatePrefix(GBool.Operator o)
        {
            if (o == GBool.Operator.Set || !IsMirroring) return true;

            MirroredSkipped++;
            return false;
        }

        private static bool OperatePrefix(GInt __instance, GInt.Operator op)
        {
            try
            {
                // A plain assignment is a statement about the world; only a relative one is a tally.
                if (op == GInt.Operator.Set) return true;

                if (IsMirroring)
                {
                    MirroredSkipped++;
                    return false;
                }

                // And only while this mod is either watching its own replicated cause or replaying a
                // teammate's. An add that happens for some unrelated reason says nothing.
                if (!IsInsideReplicatedCause) return true;

                // A replicated haul is explicitly not such a window; see HaulScope. Without this the
                // far machine would enrol the whole consequence cascade of the tally reaching zero as
                // derived, and the machine that actually dumped would not - and a variable one peer
                // broadcasts while the other refuses it is worse than either behaviour alone.
                if (RemoteApply<HaulScope>.InProgress) return true;

                Mark(__instance);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[GVar] watching a relative write failed: " + e);
            }

            return true;
        }

        private static void Mark(AGVarBase gvar)
        {
            if (gvar == null) return;

            GVarList parent = gvar.GetParent();
            if (parent == null) return;

            string listGuid = parent.Guid;
            string varId = gvar.Id;
            if (string.IsNullOrEmpty(listGuid) || string.IsNullOrEmpty(varId)) return;

            if (!_derived.Add(Key(listGuid, varId))) return;

            MelonLogger.Msg($"[GVar] '{parent.name}.{gvar.name}' is a tally of work this session " +
                "replicates; this world will keep its own count rather than copy anybody else's.");
        }

        private static string Key(string listGuid, string varId)
        {
            return listGuid + "/" + varId;
        }
    }
}
