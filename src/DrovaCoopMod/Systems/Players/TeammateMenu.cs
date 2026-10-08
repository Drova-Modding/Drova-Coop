using Drova_Modding_API;
using Drova_Modding_API.Systems.Dialogues;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.Players.Patches;
using Il2CppDrova;
using Il2CppDrova.DialogueNew;
using Il2CppNodeCanvas.DialogueTrees;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Makes a teammate's body talkable like an NPC, offering trade, duel and arena team. The choice is carried out
    /// once the dialogue closes (<see cref="Trade"/>, <see cref="Invites"/>).
    /// </summary>
    internal sealed class TeammateMenu : ICoopSystem
    {
        private const string LocaPath = "DrovaCoop/Teammate";
        private const string TreeName = "DrovaCoop_Teammate";

        private static readonly Dictionary<string, string> Lines = new(StringComparer.Ordinal)
        {
            ["Greet"] = "Yes?",
            ["Trade"] = "Let's trade.",
            ["Duel"] = "I challenge you to a duel.",
            ["Team"] = "Join my arena team.",
            ["Bye"] = "Never mind.",
            ["AckTrade"] = "Let's see.",
            ["AckDuel"] = "We'll see about that.",
            ["AckTeam"] = "Let me think about it.",
        };

        // A choice is heard by its reply line being localized, since DS_MultipleChoiceNode.OnOptionSelected is inlined.
        private static readonly Dictionary<string, int> ChoiceByReply = new(StringComparer.Ordinal)
        {
            ["AckTrade"] = 0,
            ["AckDuel"] = 1,
            ["AckTeam"] = 2,
        };

        private static readonly Dictionary<IntPtr, Talkable> ByBody = new();

        private static PlayerId _chosenFor = PlayerId.None;
        private static int _chosen = -1;

        private sealed class Talkable
        {
            internal PlayerId Owner;
            internal InteractionRefHelper? Helper;
            internal DialogueTree? Tree;
        }

        /// <summary>Menu choices carried out. For tests.</summary>
        internal static int Chosen { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, HarmonyLib.AccessTools.Method(typeof(Il2CppCustomFramework.Localization.LocalizationDB), "Localize",
                [typeof(string), typeof(string), typeof(Il2CppSystem.Object), typeof(bool)]), typeof(TeammateMenuPatches), nameof(TeammateMenuPatches.LocalizePrefix), "LocalizationDB.Localize");
        }

        internal static bool LocalizePrefix(string locaPath, string? locaKey, ref string __result)
        {
            if (locaPath != LocaPath || locaKey == null || !Lines.TryGetValue(locaKey, out string line)) return true;

            __result = line;
            if (ChoiceByReply.TryGetValue(locaKey, out int choice)) Heard(choice);
            return false;
        }

        private static void Heard(int choice)
        {
            try
            {
                Actor? owner = DialogueSystemGameHandler.TryGet(out DialogueSystemGameHandler dialogues) ? dialogues.ActivePlayerDialogue?.GetOwner() : null;
                if (owner == null || !ByBody.TryGetValue(owner.Pointer, out Talkable talkable)) return;

                _chosenFor = talkable.Owner;
                _chosen = choice;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Menu] reading a teammate menu choice failed: " + e);
            }
        }

        /// <summary>Makes a teammate's body talkable. Called once the body exists.</summary>
        internal static void Attach(Actor body, PlayerId owner)
        {
            try
            {
                if (body == null || ByBody.ContainsKey(body.Pointer)) return;

                Talkable talkable = new() { Owner = owner };

                InteractionRefHelper helper = InteractionActorPooling.ClaimFromPool(body.FeetTransform);
                helper.SetActor(body, true);
                helper.DisableBrawl(true);
                body.SetInteractionRefHelper(helper);
                talkable.Helper = helper;

                DS_DialogueTreeController? controller = body.gameObject.GetComponent<DS_DialogueTreeController>();
                if (controller == null)
                {
                    Component? added = body.gameObject.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<DS_DialogueTreeController>());
                    if (added != null) controller = added.TryCast<DS_DialogueTreeController>();
                }
                if (controller == null) return;

                controller.SetOwner(body);
                talkable.Tree = BuildTree(body);
                controller.graph = talkable.Tree;
                body.DialogueModule.SetDialogueTreeController(controller);
                // Not marked "interactable": the hit logic treats an interactable body as an NPC and drops friendly fire.

                ByBody[body.Pointer] = talkable;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Menu] making a teammate talkable failed: " + e);
            }
        }

        /// <summary>Gives the pooled helper back. Called before a teammate's body is destroyed.</summary>
        /// <remarks>Also drops the entry of an already destroyed body, so a new body at the reused address is attached.</remarks>
        internal static void Detach(Actor? body)
        {
            if (body is null || !ByBody.Remove(body.Pointer, out Talkable talkable)) return;

            try
            {
                if (body != null) body.SetInteractionRefHelper(null);
                if (talkable.Helper != null) InteractionActorPooling.ReleaseToPool(talkable.Helper);
                if (talkable.Tree != null) UnityEngine.Object.Destroy(talkable.Tree);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Menu] releasing a teammate's interaction failed: " + e);
            }
        }

        /// <summary>Carries out a choice once the conversation has closed.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (_chosen < 0) return;

            if (DialogueSystemGameHandler.TryGet(out DialogueSystemGameHandler dialogues) && dialogues != null && dialogues.IsPlayerDialogueRunning()) return;

            int choice = _chosen;
            PlayerId owner = _chosenFor;
            _chosen = -1;
            _chosenFor = PlayerId.None;

            switch (choice)
            {
                case 0:
                    Chosen++;
                    MelonLogger.Msg("[Menu] " + Trade.OpenWith(owner));
                    break;
                case 1:
                    Chosen++;
                    Invites.Ask(owner, InviteMessage.KindDuel);
                    break;
                case 2:
                    Chosen++;
                    Invites.Ask(owner, InviteMessage.KindTeam);
                    break;
            }
        }

        private static DialogueTree BuildTree(Actor body)
        {
            DialogueTree tree = ScriptableObject.CreateInstance<DialogueTree>();
            tree.name = TreeName;
            tree.actorParameters = new Il2CppSystem.Collections.Generic.List<DialogueTree.ActorParameter>();

            string speakerId = Il2CppSystem.Guid.NewGuid().ToString();
            DialogueTree.ActorParameter speaker = new() { _keyName = "Teammate", Actor = null, ActorGuid = body.GetGuidComponent()._guidString, _id = speakerId };
            DialogueTree.ActorParameter player = ActorParameterHelper.GetPlayerActorParameters();
            tree.actorParameters.Add(speaker);
            tree.actorParameters.Add(player);

            DialogueNodeCreator nodes = new(tree, player);
            DS_StatementNode greet = nodes.CreateStatementNode(Line("Greet"), speaker);
            DS_MultipleChoiceNode choices = nodes.CreateMultipleChoices(
            [
                new ChoiceCreationData { Statement = Line("Trade") },
                new ChoiceCreationData { Statement = Line("Duel") },
                new ChoiceCreationData { Statement = Line("Team") },
                new ChoiceCreationData { Statement = Line("Bye") },
            ]);

            tree.ConnectNodes(greet, choices);

            // Choice i follows connection i, so every choice needs one.
            foreach (string reply in (string[])["AckTrade", "AckDuel", "AckTeam"])
            {
                DS_StatementNode answer = nodes.CreateStatementNode(Line(reply), speaker);
                tree.ConnectNodes(choices, answer);
                tree.ConnectNodes(answer, nodes.CreateFinishNode());
            }

            tree.ConnectNodes(choices, nodes.CreateFinishNode());
            tree.primeNode = greet;
            tree.Serialize(null);
            return tree;
        }

        private static DS_Statement Line(string key) => new() { useGlobalLoca = true, GlobalLocaPath = LocaPath, locaKey = key };
    }
}
