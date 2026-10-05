using Drova_Modding_API;
using Drova_Modding_API.Systems.Dialogues;
using DrovaCoopMod.Messages;
using Il2CppDrova;
using Il2CppDrova.DialogueNew;
using Il2CppNodeCanvas.DialogueTrees;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Talking to a teammate: their body here answers the interact key like an NPC, with the game's own dialogue window -
    /// "Let's trade", "I challenge you to a duel", "Join my arena team", "Never mind". The choice is carried out once the
    /// dialogue has closed (<see cref="Trade"/>, <see cref="Invites"/>).
    ///
    /// The body gets what an NPC is loaded with: the pooled interaction helper (outline, prompt, the dialogue interaction;
    /// its brawl is switched off) and a dialogue controller holding a small tree built here. The lines are this mod's own,
    /// answered by a prefix on the game's localization lookup for their path.
    /// </summary>
    internal static class TeammateMenu
    {
        private const string LocaPath = "DrovaCoop/Teammate";
        private const string TreeName = "DrovaCoop_Teammate";

        private static readonly Dictionary<string, string> _lines = new(StringComparer.Ordinal)
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

        private sealed class Talkable
        {
            internal PlayerId Owner;
            internal InteractionRefHelper? Helper;
            internal DialogueTree? Tree;
        }

        // By the body's native pointer.
        private static readonly Dictionary<IntPtr, Talkable> _byBody = new();

        private static PlayerId _chosenFor = PlayerId.None;
        private static int _chosen = -1;

        /// <summary>Menu choices carried out. For tests.</summary>
        internal static int Chosen { get; private set; }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, HarmonyLib.AccessTools.Method(typeof(Il2CppCustomFramework.Localization.LocalizationDB), "Localize",
                [typeof(string), typeof(string), typeof(Il2CppSystem.Object), typeof(bool)]), typeof(TeammateMenu), nameof(LocalizePrefix), "LocalizationDB.Localize");
        }

        // Each choice leads to its own reply line, and asking for that line's text is how a choice is heard:
        // DS_MultipleChoiceNode.OnOptionSelected is reached through a small wrapper delegate and inlined there - a patch
        // on it applied and never ran.
        private static readonly Dictionary<string, int> _choiceByReply = new(StringComparer.Ordinal)
        {
            ["AckTrade"] = 0,
            ["AckDuel"] = 1,
            ["AckTeam"] = 2,
        };

        private static bool LocalizePrefix(string locaPath, string locaKey, ref string __result)
        {
            if (locaPath != LocaPath || locaKey == null || !_lines.TryGetValue(locaKey, out string? line)) return true;

            __result = line;
            if (_choiceByReply.TryGetValue(locaKey, out int choice)) Heard(choice);
            return false;
        }

        /// <summary>A reply line is being shown: the choice before it was made, in the talk running now.</summary>
        private static void Heard(int choice)
        {
            try
            {
                Actor? owner = DialogueSystemGameHandler.TryGet(out DialogueSystemGameHandler dialogues) ? dialogues?.ActivePlayerDialogue?.GetOwner() : null;
                if (owner == null || !_byBody.TryGetValue(owner.Pointer, out Talkable? talkable)) return;

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
                if (body == null || _byBody.ContainsKey(body.Pointer)) return;

                Talkable talkable = new() { Owner = owner };

                InteractionRefHelper helper = InteractionActorPooling.ClaimFromPool(body.FeetTransform);
                helper.SetActor(body, true);
                helper.DisableBrawl(true);
                body.SetInteractionRefHelper(helper);
                talkable.Helper = helper;

                DS_DialogueTreeController controller = body.gameObject.GetComponent<DS_DialogueTreeController>() ?? body.gameObject.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<DS_DialogueTreeController>())?.TryCast<DS_DialogueTreeController>();
                controller.SetOwner(body);
                talkable.Tree = BuildTree(body);
                controller.graph = talkable.Tree;
                body.DialogueModule.SetDialogueTreeController(controller);
                // Not marked "interactable": the talk does not need it, and an interactable body is an NPC to the hit logic -
                // a teammate's real swing at it (friendly fire) stopped landing (combat_ff).

                _byBody[body.Pointer] = talkable;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Menu] making a teammate talkable failed: " + e);
            }
        }

        /// <summary>Gives the pooled helper back. Called before a teammate's body is destroyed.</summary>
        /// <remarks>Also for a body the world already destroyed: <c>is null</c>, not Unity's <c>== null</c>, so its entry is
        /// still found and dropped - left behind, a new body built at the reused address was never made talkable.</remarks>
        internal static void Detach(Actor? body)
        {
            if (body is null || !_byBody.Remove(body.Pointer, out Talkable? talkable)) return;

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

        /// <summary>Carries out a choice once the conversation has closed. Call once per frame.</summary>
        internal static void Update()
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

            // Choice i follows connection i: each needs one, or the choice node has nowhere to go and the talk ends
            // straight after the greeting. The first three answer with the reply that tells this class what was picked.
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
