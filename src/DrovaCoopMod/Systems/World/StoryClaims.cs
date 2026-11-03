using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using Il2CppDrova.DialogueNew;
using Il2CppNodeCanvas.DialogueTrees;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Runs a story scene started in a dialogue (cutscene, brawl, time jump) in one world. While a teammate's runs, this
    /// player's dialogue hubs hide the sub-dialogue it started from.
    /// Under <see cref="CoopSession.HostLeadsStory"/> clients' hubs hide every quest sub-dialogue.
    /// </summary>
    internal sealed class StoryClaims : ICoopSystem
    {
        /// <summary>How long a claim outlives its last repeat, long enough for the quest flag that closes the option to cross.</summary>
        private const float LingerSeconds = 60f;

        private const float RepeatSeconds = 5f;

        /// <summary>A teammate cannot hide more than this many trees at once.</summary>
        private const int MaxClaims = 32;

        /// <summary>How often a client may move its claim to another tree, so it cannot hide quest after quest.</summary>
        private const float SwitchSeconds = 10f;

        private const string QuestTreePrefix = "DT_Quest_";

        /// <summary>Trees a teammate's scene runs in, and the unscaled time each claim lapses.</summary>
        private static readonly Dictionary<string, float> TeammateClaims = new(StringComparer.Ordinal);

        /// <summary>Host: each client's one claimed tree and when it last switched to another.</summary>
        private static readonly Dictionary<PlayerId, (string Tree, float Since)> ClaimByPlayer = new();

        private static readonly Queue<StoryClaimMessage> Incoming = new();
        private static readonly List<string> Lapsed = new();
        private static readonly List<DialogueTree> Chain = new();

        /// <summary>The tree this player's own scene runs in, repeated to the others while it lasts.</summary>
        private static string? _own;

        private static float _sinceRepeat;

        /// <summary>Hub options hidden. Zero while a teammate's claim is live means a dead patch.</summary>
        internal static int Hidden { get; private set; }

        /// <summary>Scenes this player claimed. For tests.</summary>
        internal static int Claimed { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // Every hub option that leads into a sub-dialogue is rated here.
            Hooking.TryPrefix(harmony, typeof(DS_HubNode), "CheckPrimeNodeInSubTree",
                typeof(StoryClaimsPatches), nameof(StoryClaimsPatches.SubTreePrefix));
            Hooking.TryPostfix(harmony, typeof(DS_StartBrawlNode), "OnExecute",
                typeof(StoryClaimsPatches), nameof(StoryClaimsPatches.SceneNodePostfix));
            Hooking.TryPostfix(harmony, typeof(DS_SetDayTimeNode), "OnExecute",
                typeof(StoryClaimsPatches), nameof(StoryClaimsPatches.SceneNodePostfix));
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<StoryClaimMessage>(MessageIds.StoryClaim, OnClaim, worldOnly: true);
        }

        /// <summary>The host lets each client hold one claim and switch it only every <see cref="SwitchSeconds"/>.</summary>
        private static void OnClaim(INetPeer peer, StoryClaimMessage message)
        {
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender) || string.IsNullOrEmpty(message.Tree)) return;

                if (ClaimByPlayer.TryGetValue(sender, out (string Tree, float Since) held) && held.Tree != message.Tree)
                {
                    if (Time.unscaledTime - held.Since < SwitchSeconds) return;

                    TeammateClaims.Remove(held.Tree);
                }

                if (held.Tree != message.Tree) ClaimByPlayer[sender] = (message.Tree, Time.unscaledTime);
            }

            CoopSession.RelayAndQueue(peer, message, Incoming, CoopSession.MaxPendingFromHost, "story claims");
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            if (ClaimByPlayer.Remove(player, out (string Tree, float Since) held)) TeammateClaims.Remove(held.Tree);
        }

        /// <summary>A scene node ran here. Claims the innermost dialogue tree it runs in.</summary>
        internal static void SceneStarted()
        {
            try
            {
                if (!CoopSession.SharesWorld) return;

                List<DialogueTree> chain = RunningTrees();
                if (chain.Count == 0) return;

                string tree = chain[chain.Count - 1].name;
                if (tree == _own) return;

                _own = tree;
                _sinceRepeat = 0f;
                Claimed++;
                CoopSession.SendIfLive(new StoryClaimMessage { Tree = tree });
                MelonLogger.Msg($"[Story] a scene started in '{tree}' here; teammates cannot start it while it runs.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Story] claiming a story scene failed: " + e);
            }
        }

        /// <summary>Hides a claimed hub option the way the game hides a sub-dialogue rated Hide.</summary>
        internal static bool SubTreePrefix(DS_HubNode __instance, SubDialogueTree? subTreeNode, ref DS_MultipleChoiceNode? __result)
        {
            try
            {
                string? tree = subTreeNode == null || subTreeNode.subGraph == null ? null : subTreeNode.subGraph.name;
                if (tree == null || !Hides(tree)) return true;

                __instance.SetRatingCache(DS_HubNode.HubRating.Hide);
                __instance.subTreeNodeCache.Clear();
                __result = null;
                Hidden++;
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Story] rating a dialogue option failed: " + e);
                return true;
            }
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
            {
                if (_own != null || TeammateClaims.Count > 0) Reset();
                return;
            }

            try
            {
                while (Incoming.TryDequeue(out StoryClaimMessage claim))
                {
                    Take(claim.Tree);
                }

                Expire();
                Repeat(unscaledDeltaTime);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Story] keeping story claims failed: " + e);
            }
        }

        /// <summary>The player's running conversation, outermost first, down through the sub-dialogues it entered.</summary>
        internal static List<DialogueTree> RunningTrees()
        {
            Chain.Clear();

            if (!DialogueSystemGameHandler.TryGet(out DialogueSystemGameHandler dialogues)
                || dialogues.ActivePlayerDialogue == null || !dialogues.ActivePlayerDialogue.isRunning)
            {
                return Chain;
            }

            Il2CppNodeCanvas.Framework.Graph? graph = dialogues.ActivePlayerDialogue.graph;
            DialogueTree? tree = graph == null ? null : graph.TryCast<DialogueTree>();
            for (int depth = 0; tree != null && depth < 8; depth++)
            {
                Chain.Add(tree);

                DialogueTree? inner = tree.currentNode?.TryCast<SubDialogueTree>()?.currentInstance;
                tree = inner != null && inner.isRunning ? inner : null;
            }

            return Chain;
        }

        internal static void StopRunning()
        {
            if (DialogueSystemGameHandler.TryGet(out DialogueSystemGameHandler dialogues) && dialogues.ActivePlayerDialogue != null)
            {
                dialogues.ActivePlayerDialogue.StopDialogue();
            }
        }

        private static bool Hides(string tree)
        {
            if (!CoopSession.SharesWorld) return false;

            if (CoopSession.HostLeadsStory && CoopSession.State == CoopSessionState.Joined
                && tree.StartsWith(QuestTreePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            return TeammateClaims.TryGetValue(tree, out float lapse) && lapse > Time.unscaledTime;
        }

        private static void Take(string tree)
        {
            if (string.IsNullOrEmpty(tree)) return;
            if (!TeammateClaims.ContainsKey(tree))
            {
                if (TeammateClaims.Count >= MaxClaims) return;

                MelonLogger.Msg($"[Story] a teammate's scene runs in '{tree}'; it is hidden here while it runs.");
            }

            // Only hidden, never closed, since world scenes like the mine capture rightly run in every world at once.
            TeammateClaims[tree] = Time.unscaledTime + LingerSeconds;
        }

        private static void Expire()
        {
            if (TeammateClaims.Count == 0) return;

            Lapsed.Clear();
            foreach (KeyValuePair<string, float> claim in TeammateClaims)
            {
                if (claim.Value <= Time.unscaledTime) Lapsed.Add(claim.Key);
            }

            foreach (string tree in Lapsed)
            {
                TeammateClaims.Remove(tree);
            }
        }

        private static void Repeat(float unscaledDeltaTime)
        {
            if (_own == null) return;

            _sinceRepeat += unscaledDeltaTime;
            if (_sinceRepeat < RepeatSeconds) return;

            _sinceRepeat = 0f;
            if (!StillRuns(_own))
            {
                _own = null;
                return;
            }

            CoopSession.SendIfLive(new StoryClaimMessage { Tree = _own });
        }

        /// <summary>The talk itself, or a brawl it started here (the player's own, or a duel between NPCs).</summary>
        private static bool StillRuns(string tree)
        {
            if (EngageWatch.AnyBrawl) return true;
            if (PlayerAccess.TryGetPlayer(out Actor player) && player.CustomStats != null && player.CustomStats.IsInBrawl) return true;

            foreach (DialogueTree running in RunningTrees())
            {
                if (running.name == tree) return true;
            }

            return false;
        }

        private static void Reset()
        {
            TeammateClaims.Clear();
            ClaimByPlayer.Clear();
            Incoming.Clear();
            _own = null;
            _sinceRepeat = 0f;
        }

        void ICoopSystem.EndSession() => Reset();

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
