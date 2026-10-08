using Il2CppNodeCanvas.DialogueTrees;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Story decisions both worlds can ask at once: the first answer decides for both.
    /// A teammate's write of a deciding flag closes this player's copy of the same conversation.
    /// </summary>
    internal static class SharedChoices
    {
        private const string Stage4 = "QuestVars_RedTowerStage04";

        // Conversation tree -> the flags its deciding answers write (list.var).
        private static readonly Dictionary<string, string[]> DecidedBy = new(StringComparer.Ordinal)
        {
            // The winner's speech: challenge, or kneel.
            ["DT_Quest_RedTower_JeroBeatsMolvina_NPC_Jero"] = [Stage4 + ".M_StartPlayerFightMusic", Stage4 + ".EndStage"],
            ["DT_Quest_Molvina_AfterKilledJero_NPC_Molvina"] = [Stage4 + ".M_StartPlayerFightMusic", Stage4 + ".EndStage"],

            // After the won duel: peace, or the massacre.
            ["DT_Quest_Redtower_AfterPlayerKilledJero_NemetonSide_NPC_Eugen"] = [Stage4 + ".EndStage", Stage4 + ".StartAttackOtherFaction"],
            ["DT_Quest_Ruinenlager_AfterPlayerKilledMolvina_NPC_Henik"] = [Stage4 + ".EndStage", Stage4 + ".StartAttackOtherFaction"],
        };

        /// <summary>How many local conversations were closed because a teammate had answered first. For tests.</summary>
        internal static int Closed { get; private set; }

        /// <summary>Closes this player's copy of a conversation a teammate's write decided.</summary>
        internal static void TeammateWrote(string listName, string varName)
        {
            try
            {
                string name = listName + "." + varName;

                if (!Il2CppDrova.DialogueNew.DialogueSystemGameHandler.TryGet(out Il2CppDrova.DialogueNew.DialogueSystemGameHandler dialogues)
                    || dialogues.ActivePlayerDialogue == null || !dialogues.ActivePlayerDialogue.isRunning)
                {
                    return;
                }

                // Down through the sub-dialogues: a speech is usually entered from the NPC's hub tree.
                Il2CppNodeCanvas.Framework.Graph? graph = dialogues.ActivePlayerDialogue.graph;
                DialogueTree? tree = graph == null ? null : graph.TryCast<DialogueTree>();
                for (int depth = 0; tree != null && depth < 8; depth++)
                {
                    if (DecidedBy.TryGetValue(tree.name, out string[] flags) && flags.Contains(name))
                    {
                        dialogues.ActivePlayerDialogue.StopDialogue();
                        Closed++;
                        MelonLogger.Msg($"[Choice] a teammate answered '{tree.name}' first; this copy was closed and " +
                            "the story here follows their answer.");
                        return;
                    }

                    DialogueTree? inner = tree.currentNode?.TryCast<SubDialogueTree>()?.currentInstance;
                    tree = inner != null && inner.isRunning ? inner : null;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Choice] closing a conversation a teammate had decided failed: " + e);
            }
        }
    }
}
