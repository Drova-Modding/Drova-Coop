#if DEBUG
using Drova_Modding_API.Access;
using MelonLoader;

namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// Level one of three: does native code exist for the methods coop needs to hook?
    ///
    /// IL2CPP's AOT compiler inlines small hot methods, and when it does, Harmony's own documentation
    /// says the patch "will simply be non-functional" - silently. A design resting on a chokepoint that
    /// turns out to be inlined fails months later as a bug that looks like the game's own.
    ///
    /// This answers only the first of three questions, and it is the weakest one. A body existing does
    /// not prove Harmony can patch it (<see cref="PatchProbe.Apply"/>), and a patch applying does not
    /// prove it ever runs (<see cref="PatchProbe.Report"/>). All three are needed for an honest answer.
    ///
    /// Debug-only diagnostic. This one hooks nothing; it only reads.
    /// </summary>
    internal static class InlineProbe
    {
        /// <summary>
        /// Reports whether each target has a native body.
        /// </summary>
        /// <param name="targets">The methods under test, from <see cref="ProbeTargets.Resolve"/>.</param>
        internal static void Run(List<ProbeTargets.Target> targets)
        {
            MelonLogger.Msg("[Probe] ---- 1/3 native body ----");

            foreach (ProbeTargets.Target target in targets)
            {
                bool hasBody = QuestStateReader.HasNativeBody(target.Method, out string detail);
                string verdict = hasBody ? "BODY   " : "NO BODY";

                MelonLogger.Msg($"[Probe] {verdict} {target.Group}: {target.Label}  [{detail}]");
            }
        }
    }
}
#endif
