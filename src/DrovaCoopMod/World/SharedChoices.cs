using Il2CppNodeCanvas.DialogueTrees;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Story decisions both worlds can put to their player at once: the first answer decides for both.
    ///
    /// The Red Tower's leader speech (kneel or challenge) and the talk after the duel (peace or massacre) start
    /// from an intent that is raised in both worlds, so both players could be asked - and one could kneel while the
    /// other challenged. The answer is known by the flags it writes; when a teammate's write of one of them lands
    /// while this player still stands in the same conversation, this copy is closed and the story here follows
    /// the teammate's answer through those same flags.
    /// </summary>
    internal static class SharedChoices
    {
        private const string Stage4 = "QuestVars_RedTowerStage04";

        // Conversation tree -> the flags its deciding answers write (list.var).
        private static readonly Dictionary<string, string[]> _decidedBy = new(StringComparer.Ordinal)
        {
            // The winner's speech: challenge, or kneel. Nemeton side (Jero speaks), Remnants side (Molvina speaks).
            ["DT_Quest_RedTower_JeroBeatsMolvina_NPC_Jero"] = [Stage4 + ".M_StartPlayerFightMusic", Stage4 + ".EndStage"],
            ["DT_Quest_Molvina_AfterKilledJero_NPC_Molvina"] = [Stage4 + ".M_StartPlayerFightMusic", Stage4 + ".EndStage"],

            // After the won duel: peace, or the massacre.
            ["DT_Quest_Redtower_AfterPlayerKilledJero_NemetonSide_NPC_Eugen"] = [Stage4 + ".EndStage", Stage4 + ".StartAttackOtherFaction"],
            ["DT_Quest_Ruinenlager_AfterPlayerKilledMolvina_NPC_Henik"] = [Stage4 + ".EndStage", Stage4 + ".StartAttackOtherFaction"],
        };

        /// <summary>How many local conversations were closed because a teammate had answered first. For tests.</summary>
        internal static int Closed { get; private set; }

        /// <summary>
        /// Called when a teammate's write lands here. Closes this player's copy of a conversation that write decided.
        /// </summary>
        internal static void TeammateWrote(string listName, string varName)
        {
            try
            {
                string name = listName + "." + varName;

                if (!Il2CppDrova.DialogueNew.DialogueSystemGameHandler.TryGet(out Il2CppDrova.DialogueNew.DialogueSystemGameHandler dialogues)
                    || dialogues?.ActivePlayerDialogue == null || !dialogues.ActivePlayerDialogue.isRunning)
                {
                    return;
                }

                // Down through the sub-dialogues: a speech is usually entered from the NPC's hub tree.
                DialogueTree? tree = dialogues.ActivePlayerDialogue.graph?.TryCast<DialogueTree>();
                for (int depth = 0; tree != null && depth < 8; depth++)
                {
                    if (_decidedBy.TryGetValue(tree.name, out string[]? flags) && flags.Contains(name))
                    {
                        dialogues.ActivePlayerDialogue.StopDialogue();
                        Closed++;
                        MelonLogger.Msg($"[Choice] a teammate answered '{tree.name}' first; this copy was closed and " +
                            "the story here follows their answer.");
                        return;
                    }

                    tree = tree.currentNode?.TryCast<SubDialogueTree>()?.currentInstance is { isRunning: true } inner ? inner : null;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Choice] closing a conversation a teammate had decided failed: " + e);
            }
        }
    }
}
