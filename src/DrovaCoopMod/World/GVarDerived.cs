using Drova_Modding_API;
using DrovaCoopMod.Sync;
using Il2CppDrova.GlobalVarSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Remembers which global variables are tallies this world keeps itself, so they are never copied from another.
    /// A variable written by an add or subtract during replicated work is a tally, and both machines mark it independently.
    /// </summary>
    internal static class GVarDerived
    {
        private static readonly HashSet<string> Derived = new(StringComparer.Ordinal);

        /// <summary>How many variables were found to be tallies. For <c>coop_status</c>.</summary>
        internal static int Count => Derived.Count;

        /// <summary>How many arriving absolutes were refused because of that. For <c>coop_status</c>.</summary>
        internal static int RefusedAbsolutes { get; private set; }

        /// <summary>Relative writes dropped because this machine was only mirroring a teammate's event. For <c>coop_status</c>.</summary>
        internal static int MirroredSkipped { get; private set; }

        /// <summary>Whether the discovery hook is in place.</summary>
        internal static bool Hooked { get; private set; }

        // Quest-graph relative operations already counted for the party, as "graph/uid".
        // ponytail: session memory only, a reload in between counts again; a GrantLedger entry would close that.
        private static readonly HashSet<string> Operations = new(StringComparer.Ordinal);

        /// <summary>The quest-graph operation running now, for the change it causes to carry. See GVarChangeMessage.Operation.</summary>
        internal static string? CurrentOperation { get; private set; }

        /// <summary>Relative graph operations skipped because a teammate's had counted already. For tests.</summary>
        internal static int OperationsSkipped { get; private set; }

        /// <summary>Records a teammate's graph operation so this machine's copy of the node is skipped.</summary>
        internal static void NoteRemoteOperation(string? operation)
        {
            if (!string.IsNullOrEmpty(operation)) Operations.Add(operation);
        }

        /// <summary>Calls of GBool.Operate seen. Zero after a quest step means the hook never runs. For tests.</summary>
        internal static int BoolOperations { get; private set; }

        /// <summary>Whether the GBool hook applied at all.</summary>
        internal static bool BoolHooked { get; private set; }

        /// <summary>Every variable found to be a tally, as "List.Var". For <c>coop_gvar_derived</c>.</summary>
        internal static IEnumerable<string> Names => Derived;

        /// <summary>Forgets everything. Called when a session ends and after a savegame load.</summary>
        internal static void Reset()
        {
            Derived.Clear();
            RefusedAbsolutes = 0;
            MirroredSkipped = 0;
            Operations.Clear();
            CurrentOperation = null;
        }

        /// <summary>Applies the discovery hook. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooked = Hooking.TryPrefix(harmony, typeof(GInt), nameof(GInt.Operate),
                typeof(GVarDerived), nameof(OperatePrefix));

            Hooking.TryPostfix(harmony, typeof(GInt), nameof(GInt.Operate), typeof(GVarDerived), nameof(OperatePostfix));

            BoolHooked = Hooking.TryPrefix(harmony, typeof(GBool), nameof(GBool.Operate),
                typeof(GVarDerived), nameof(BoolOperatePrefix));

            if (!Hooked)
            {
                MelonLogger.Warning("[GVar] the derived-variable hook did not apply; quest tallies " +
                    "such as the mine's rubble count will be replicated as absolutes and will fight " +
                    "between the two worlds.");
            }
        }

        /// <summary>Whether a variable is this world's own tally.</summary>
        internal static bool IsDerived(string listGuid, string varId)
        {
            return Derived.Count > 0 && Derived.Contains(Key(listGuid, varId));
        }

        /// <summary>Counts an arriving absolute that was refused because the variable is a tally.</summary>
        internal static void CountRefusal()
        {
            RefusedAbsolutes++;
        }

        /// <summary>Whether this machine is inside a cause this mod replicates, so a relative write now is a tally of it.</summary>
        internal static bool IsInsideReplicatedCause
        {
            get => ResourceWork.IsLocalWorkInProgress || RemoteApply<WorkScope>.InProgress;
        }

        /// <summary>
        /// Whether this machine is only mirroring an event a teammate's world already resolved, whose relative writes would count twice.
        /// Not <see cref="EnemyBlowScope"/>, since a relayed blow resolves the fight here for the first time.
        /// </summary>
        private static bool IsMirroring
        {
            get => RemoteApply<EnemyHealthScope>.InProgress || RemoteApply<StoryScope>.InProgress;
        }

        /// <summary>"graph/uid" for an operation a graph node makes, else null.</summary>
        private static string? OperationOf(GVarContext context)
        {
            if (context == null || !context.HasMessage) return null;
            Il2CppNodeCanvas.Framework.Graph? graph = context.Context?.TryCast<Il2CppNodeCanvas.Framework.Graph>();
            return graph == null ? null : graph.name + "/" + context.Message;
        }

        private static bool BoolOperatePrefix(GBool.Operator o)
        {
            BoolOperations++;
            if (o == GBool.Operator.Set || !IsMirroring) return true;

            MirroredSkipped++;
            return false;
        }

        private static void OperatePostfix() => CurrentOperation = null;

        private static bool OperatePrefix(GInt __instance, GInt.Operator op, GVarContext context)
        {
            try
            {
                if (op == GInt.Operator.Set) return true;

                // Every machine's copy of a quest graph runs the node, so it is counted once for the party.
                string? operation = OperationOf(context);
                if (operation != null)
                {
                    if (!Operations.Add(operation))
                    {
                        OperationsSkipped++;
                        return false;
                    }

                    CurrentOperation = operation;
                }

                if (IsMirroring)
                {
                    MirroredSkipped++;
                    return false;
                }

                if (!IsInsideReplicatedCause) return true;

                // A replicated haul is not such a window, or only the far machine would mark its cascade as derived.
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

            if (!Derived.Add(Key(listGuid, varId))) return;

            MelonLogger.Msg($"[GVar] '{parent.name}.{gvar.name}' is a tally of work this session " +
                "replicates; this world will keep its own count rather than copy anybody else's.");
        }

        private static string Key(string listGuid, string varId)
        {
            return listGuid + "/" + varId;
        }
    }
}
