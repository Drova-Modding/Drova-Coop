using Drova_Modding_API;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova.GlobalVarSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Remembers which global variables are tallies this world keeps itself, so they are never copied from another.
    /// A variable written by an add or subtract during replicated work is a tally, and both machines mark it independently.
    /// </summary>
    internal sealed class GVarDerived : ICoopSystem
    {
        private static readonly HashSet<string> Derived = new(StringComparer.Ordinal);

        // Quest-graph relative operations already counted for the party, as "graph/uid".
        // Session memory only: an operation counted before a reload counts again after it.
        private static readonly HashSet<string> Operations = new(StringComparer.Ordinal);

        /// <summary>How many variables were found to be tallies. For <c>coop_status</c>.</summary>
        internal static int Count => Derived.Count;

        /// <summary>How many arriving absolutes were refused because of that. For <c>coop_status</c>.</summary>
        internal static int RefusedAbsolutes { get; private set; }

        /// <summary>Relative writes dropped because this machine was only mirroring a teammate's event. For <c>coop_status</c>.</summary>
        internal static int MirroredSkipped { get; private set; }

        /// <summary>Whether the discovery hook is in place.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>The quest-graph operation running now, carried by the change it causes (<see cref="Messages.GVarChangeMessage.Operation"/>).</summary>
        internal static string? CurrentOperation { get; private set; }

        /// <summary>Relative graph operations skipped because a teammate's had counted already. For tests.</summary>
        internal static int OperationsSkipped { get; private set; }

        /// <summary>Calls of GBool.Operate seen. Zero after a quest step means the hook never runs. For tests.</summary>
        internal static int BoolOperations { get; private set; }

        /// <summary>Every variable found to be a tally, as "listGuid/varId". For <c>coop_gvar_derived</c>.</summary>
        internal static IEnumerable<string> Names => Derived;

        /// <summary>Records a teammate's graph operation so this machine's copy of the node is skipped.</summary>
        internal static void NoteRemoteOperation(string? operation)
        {
            if (!string.IsNullOrEmpty(operation)) Operations.Add(operation);
        }

        /// <summary>Forgets everything. Called when a session ends and after a savegame load.</summary>
        private static void Reset()
        {
            Derived.Clear();
            RefusedAbsolutes = 0;
            MirroredSkipped = 0;
            Operations.Clear();
            CurrentOperation = null;
        }

        /// <summary>Applies the discovery hook.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooked = Hooking.TryPrefix(harmony, typeof(GInt), nameof(GInt.Operate),
                typeof(GVarDerivedPatches), nameof(GVarDerivedPatches.OperatePrefix));

            Hooking.TryPostfix(harmony, typeof(GInt), nameof(GInt.Operate), typeof(GVarDerivedPatches), nameof(GVarDerivedPatches.OperatePostfix));

            Hooking.TryPrefix(harmony, typeof(GBool), nameof(GBool.Operate),
                typeof(GVarDerivedPatches), nameof(GVarDerivedPatches.BoolOperatePrefix));

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

        /// <summary>Whether a variable is this world's own tally, by its "listGuid/varId" key.</summary>
        internal static bool IsDerived(string key)
        {
            return Derived.Count > 0 && Derived.Contains(key);
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

        /// <summary>"graph/uid" for an operation a quest graph node makes, else null.</summary>
        private static string? OperationOf(GVarContext? context)
        {
            if (context == null || !context.HasMessage) return null;
            Il2CppNodeCanvas.Framework.Graph? graph = context.Context?.TryCast<Il2CppNodeCanvas.Framework.Graph>();

            // A dialogue runs only on the talker's machine and its nodes repeat on every visit, so it is never counted once.
            if (graph == null || graph.TryCast<Il2CppNodeCanvas.DialogueTrees.DialogueTree>() != null) return null;
            return graph.name + "/" + context.Message;
        }

        internal static bool BoolOperatePrefix(GBool.Operator o)
        {
            BoolOperations++;
            if (o == GBool.Operator.Set || !IsMirroring) return true;

            MirroredSkipped++;
            return false;
        }

        internal static void OperatePostfix() => CurrentOperation = null;

        internal static bool OperatePrefix(GInt __instance, GInt.Operator op, GVarContext context)
        {
            try
            {
                if (op == GInt.Operator.Set || !CoopSession.SharesWorld) return true;

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

        private static void Mark(AGVarBase? gvar)
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

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
