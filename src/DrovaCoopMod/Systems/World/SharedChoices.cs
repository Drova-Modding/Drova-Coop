using Il2CppNodeCanvas.DialogueTrees;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Story decisions both worlds can ask at once. The first answer decides for both.
    /// </summary>
    internal static class SharedChoices
    {
        private const string Stage4 = "QuestVars_RedTowerStage04";

        /// <summary>Conversation tree to the list.var flags its deciding answers write.</summary>
        private static readonly Dictionary<string, string[]> DecidedBy = new(StringComparer.Ordinal)
        {
            // The winner's speech. Challenge or kneel.
            ["DT_Quest_RedTower_JeroBeatsMolvina_NPC_Jero"] = [Stage4 + ".M_StartPlayerFightMusic", Stage4 + ".EndStage"],
            ["DT_Quest_Molvina_AfterKilledJero_NPC_Molvina"] = [Stage4 + ".M_StartPlayerFightMusic", Stage4 + ".EndStage"],

            // After the won duel. Peace or the massacre.
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

                // A speech is usually entered from the NPC's hub tree.
                foreach (DialogueTree tree in StoryClaims.RunningTrees())
                {
                    if (!DecidedBy.TryGetValue(tree.name, out string[] flags) || !flags.Contains(name)) continue;

                    StoryClaims.StopRunning();
                    Closed++;
                    MelonLogger.Msg($"[Choice] a teammate answered '{tree.name}' first; this copy was closed and " +
                        "the story here follows their answer.");
                    return;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Choice] closing a conversation a teammate had decided failed: " + e);
            }
        }
    }
}
