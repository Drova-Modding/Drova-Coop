#if DEBUG
using System.Reflection;
using Drova_Modding_API;
using MelonLoader;

namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// Levels two and three: does the patch apply, and does it ever run?
    ///
    /// These are separate failures with separate causes. Applying can fail where a body exists - a ref
    /// parameter on an Il2Cpp method can make Harmony generate invalid IL, and that surfaces as an
    /// exception at patch time. Running can fail where applying succeeded, because IL2CPP may emit a
    /// method body and inline the same method at some call sites; the patch is then installed on a
    /// body those callers never enter. That is the dangerous one: it looks like a working hook that
    /// misses some events, which in a coop mod reads as a desync bug rather than a hook bug.
    ///
    /// Nothing here changes behavior. Every patch is a postfix that increments a counter.
    /// </summary>
    internal static class PatchProbe
    {
        private static readonly Dictionary<MethodBase, int> _calls = new();
        private static readonly List<ProbeTargets.Target> _applied = new();

        /// <summary>
        /// Applies a counting postfix to every target and reports which took the patch.
        ///
        /// Goes through the API's <see cref="Hooking"/> rather than Harmony directly, so a target that
        /// cannot be patched is isolated and logged instead of taking the rest of the set down with it -
        /// which is the whole reason that helper exists.
        /// </summary>
        /// <param name="harmony">The melon's Harmony instance.</param>
        /// <param name="targets">The methods under test.</param>
        internal static void Apply(HarmonyLib.Harmony harmony, List<ProbeTargets.Target> targets)
        {
            MelonLogger.Msg("[Probe] ---- 2/3 patch applies ----");

            foreach (ProbeTargets.Target target in targets)
            {
                bool applied = Hooking.TryPostfix(harmony, target.Method, typeof(PatchProbe), nameof(CountingPostfix), target.Label);
                MelonLogger.Msg($"[Probe] {(applied ? "APPLIED" : "REFUSED")} {target.Group}: {target.Label}");

                if (applied)
                {
                    _applied.Add(target);
                }
            }

            MelonLogger.Msg("[Probe] Now play: take a hit, heal, pick an item up, trip a quest flag. Then press F10.");
        }

        /// <summary>
        /// Writes how often each applied patch actually ran. A count of zero is the interesting result:
        /// it means the patch installed on a body nothing reaches, so far.
        ///
        /// Zero is only evidence once the corresponding thing has been done in game - an untouched
        /// counter for a method whose trigger never happened says nothing at all, and the log line says
        /// so rather than implying a verdict.
        /// </summary>
        internal static void Report()
        {
            MelonLogger.Msg("[Probe] ---- 3/3 patch fires ----");

            if (_applied.Count == 0)
            {
                MelonLogger.Msg("[Probe] nothing was patched, so there is nothing to count.");
                return;
            }

            foreach (ProbeTargets.Target target in _applied)
            {
                int count = _calls.TryGetValue(target.Method, out int value) ? value : 0;
                string verdict = count > 0 ? $"RAN {count,5}x" : "NEVER RAN ";

                MelonLogger.Msg($"[Probe] {verdict} {target.Group}: {target.Label}");
            }

            MelonLogger.Msg("[Probe] A zero only counts as evidence if that thing was actually done in game.");
        }

        /// <summary>
        /// The postfix every target shares. Harmony injects the patched method as
        /// <c>__originalMethod</c>, which is what lets one counter serve all of them; taking none of the
        /// original's own parameters also keeps this test about the target's signature rather than about
        /// whether the probe marshalled it correctly.
        /// </summary>
        private static void CountingPostfix(MethodBase __originalMethod)
        {
            try
            {
                if (__originalMethod == null) return;

                _calls[__originalMethod] = (_calls.TryGetValue(__originalMethod, out int count) ? count : 0) + 1;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Probe] counting postfix failed: " + e);
            }
        }
    }
}
#endif
