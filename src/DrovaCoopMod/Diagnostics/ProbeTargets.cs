#if DEBUG
using System.Reflection;
using HarmonyLib;
using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.InventorySystem;
using Il2CppDrova.QuestSystem;
using MelonLoader;

namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// The methods coop needs to hook, in one place, so the body probe and the patch probe are provably
    /// asking about the same set. Two lists would drift, and a target that silently fell out of one of
    /// them would look like a passing answer rather than a missing question.
    /// </summary>
    internal static class ProbeTargets
    {
        /// <summary>
        /// One method under test.
        /// </summary>
        /// <param name="Group">Which subsystem it belongs to, for reading the log.</param>
        /// <param name="Label">Human-readable name including parameter types.</param>
        /// <param name="Method">The resolved interop proxy method.</param>
        internal readonly record struct Target(string Group, string Label, MethodInfo Method);

        /// <summary>
        /// Resolves every candidate. Resolution failures are logged and dropped rather than thrown, so
        /// one missing method cannot hide the answers for all the others.
        /// </summary>
        internal static List<Target> Resolve()
        {
            List<Target> targets = new();

            // Health has two independent write paths, confirmed by decompiling Managed\Drova.dll:
            // SetHealth writes _curHealth directly and never calls ChangeHealth, so patching only the
            // latter would miss every heal, scripted set and percent change. CheckEvents is the one
            // funnel both share, and is here because its ref parameter is the signature most likely to
            // fail at patch time rather than at body lookup.
            Add(targets, "Health", typeof(Health), nameof(Health.ChangeHealth));
            Add(targets, "Health", typeof(Health), nameof(Health.SetHealth));
            Add(targets, "Health", typeof(Health), "CheckEvents");

            // Each closed generic is a separate native method, so each is asked separately.
            Add(targets, "GVar", typeof(AGVar<bool>), nameof(AGVar<bool>.SetValue));
            Add(targets, "GVar", typeof(AGVar<int>), nameof(AGVar<int>.SetValue));
            Add(targets, "GVar", typeof(AGVar<float>), nameof(AGVar<float>.SetValue));
            Add(targets, "GVar", typeof(AGVar<QuestState>), nameof(AGVar<QuestState>.SetValue));

            // AddItem(ItemStack, bool, bool) is the funnel: AddItem(ItemStack) and both AddItems
            // overloads all call into it. The others are probed anyway to prove that stays true, but
            // only this one may ever be patched, or a single pickup fires the hook twice.
            Add(targets, "Inventory", typeof(Inventory), nameof(Inventory.AddItem));
            Add(targets, "Inventory", typeof(Inventory), nameof(Inventory.AddItems));
            Add(targets, "Inventory", typeof(Inventory), nameof(Inventory.RemoveItem));

            // The pause path. A prefix on RequestGamePause applies cleanly and the world still freezes,
            // which is the signature of a patch that was accepted and never runs. Measuring it here says
            // whether that is what is happening, rather than leaving it inferred.
            Add(targets, "Pause", typeof(GameState_TimeScale), nameof(GameState_TimeScale.RequestGamePause));
            Add(targets, "Pause", typeof(GameState_TimeScale), nameof(GameState_TimeScale.ReleaseGamePause));
            Add(targets, "Pause", typeof(GameState_TimeScale), "CheckIsGamePaused");

            return targets;
        }

        /// <summary>
        /// Adds every overload of <paramref name="methodName"/>. By name rather than by signature,
        /// because a private overload or one whose parameters the interop generator rendered
        /// differently still has to be reported instead of being missed as "not found".
        /// </summary>
        private static void Add(List<Target> targets, string group, Type type, string methodName)
        {
            try
            {
                MethodInfo[] overloads = type.GetMethods(AccessTools.all)
                    .Where(method => method.Name == methodName)
                    .ToArray();

                if (overloads.Length == 0)
                {
                    MelonLogger.Warning($"[Probe] {group}: {type.Name}.{methodName} - NO PROXY, the interop assembly has no such method.");
                    return;
                }

                foreach (MethodInfo overload in overloads)
                {
                    targets.Add(new Target(group, type.Name + "." + Describe(overload), overload));
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Probe] {group}: resolving {type.Name}.{methodName} threw {e.GetType().Name}: {e.Message}");
            }
        }

        private static string Describe(MethodInfo method)
        {
            IEnumerable<string> parameters = method.GetParameters()
                .Select(parameter => (parameter.ParameterType.IsByRef ? "ref " : "") + parameter.ParameterType.Name.TrimEnd('&'));

            return method.Name + "(" + string.Join(", ", parameters) + ")";
        }
    }
}
#endif
