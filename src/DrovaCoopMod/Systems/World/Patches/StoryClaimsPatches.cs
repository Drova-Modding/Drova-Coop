using Il2CppNodeCanvas.DialogueTrees;

namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>Harmony entry points of <see cref="StoryClaims"/>.</summary>
    internal static class StoryClaimsPatches
    {
        internal static bool SubTreePrefix(DS_HubNode __instance, SubDialogueTree? subTreeNode, ref DS_MultipleChoiceNode? __result)
            => StoryClaims.SubTreePrefix(__instance, subTreeNode, ref __result);

        internal static void SceneNodePostfix()
            => StoryClaims.SceneStarted();
    }
}
