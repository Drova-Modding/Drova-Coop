using Il2CppNodeCanvas.DialogueTrees;
using Il2CppNodeCanvas.DialogueTrees.UI;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="SpokenLines"/>. Each forwards to the system.</summary>
    internal static class SpokenLinesPatches
    {
        internal static void SubtitlesPrefix(DS_DialogueUGUI __instance, SubtitlesRequestInfo? info)
            => SpokenLines.SubtitlesPrefix(__instance, info);

        internal static void FinalizePrefix(DS_DialogueUGUI __instance, MultipleChoiceRequestInfo? info, int index)
            => SpokenLines.FinalizePrefix(__instance, info, index);

        internal static bool LocalizePrefix(string locaPath, string locaKey, ref string __result)
            => SpokenLines.LocalizePrefix(locaPath, locaKey, ref __result);
    }
}
