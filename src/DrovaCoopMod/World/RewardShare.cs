using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Pays everybody the experience a dialogue granted one player.
    ///
    /// A dialogue's reward node pays the player standing in the conversation - the far half of a
    /// shared quest gets the story and none of the reward. Kill experience has its own share with
    /// per-creature dedup; a dialogue grant has no identity to dedup by, so it travels like a
    /// cutscene: relayed with the sender excluded, because paying twice is not a no-op.
    /// </summary>
    internal static class RewardShare
    {
        /// <summary>
        /// Reward nodes already paid here, by dialogue tree plus node id. Both players sitting
        /// through the same conversation is one reward, not two: the second local grant is zeroed
        /// and the second arriving grant is dropped.
        /// </summary>
        private static readonly HashSet<string> _paid = new(StringComparer.Ordinal);

        private static int _shared;
        private static int _received;

        /// <summary>How many grants this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared => _shared;

        /// <summary>How many grants arrived and were paid here. For <c>coop_status</c>.</summary>
        internal static int Received => _received;

        /// <summary>Forgets the counts and what was paid. Called when a session ends.</summary>
        internal static void Reset()
        {
            _paid.Clear();
            _shared = 0;
            _received = 0;
        }

        /// <summary>Applies the reward patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp), "OnExecute",
                typeof(RewardShare), nameof(GiveExpPrefix));
            Hooking.TryPostfix(harmony, typeof(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp), "OnExecute",
                typeof(RewardShare), nameof(GiveExpPostfix));
        }

        /// <summary>
        /// Pays everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Reset();
                return;
            }

            while (CoopSession.TryTakeDialogueExp(out DialogueExpMessage message))
            {
                Apply(message);
            }
        }

        private static void Apply(in DialogueExpMessage message)
        {
            try
            {
                if (!message.IsPlausible()) return;

                // Already paid, by this player sitting through the conversation themselves or by an
                // earlier copy of this grant. An identityless grant cannot be deduplicated and is
                // paid as it comes.
                if (!string.IsNullOrEmpty(message.Identity) && !_paid.Add(message.Identity)) return;

                if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return;

                PlayerAttributeStats? stats = player.GetStats()?.TryCast<PlayerAttributeStats>();

                if (stats == null) return;

                stats.AddExperiencePoints(message.Amount);
                _received++;

                MelonLogger.Msg($"[Exp] {message.Amount} experience arrived from a teammate's dialogue.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] paying a teammate's dialogue reward failed: " + e);
            }
        }

        /// <summary>
        /// Zeroes a reward this world already collected, so the node grants nothing and the dialogue
        /// still continues. The amount is stashed for the postfix to restore.
        /// </summary>
        private static void GiveExpPrefix(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp __instance,
            out int __state)
        {
            __state = 0;

            try
            {
                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (__instance == null) return;

                string identity = IdentityOf(__instance);
                if (string.IsNullOrEmpty(identity) || !_paid.Contains(identity)) return;

                __state = __instance.ExpPoints;
                __instance.ExpPoints = 0;

                MelonLogger.Msg("[Exp] a dialogue reward this world already collected was skipped, " +
                    "because a teammate's copy of the same conversation paid it first.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] refusing a repeated dialogue reward failed: " + e);
            }
        }

        private static void GiveExpPostfix(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp __instance, int __state)
        {
            try
            {
                if (__instance == null) return;

                // The prefix zeroed a repeat: put the authored figure back and report nothing.
                if (__state > 0)
                {
                    __instance.ExpPoints = __state;
                    return;
                }

                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;

                int amount = __instance.ExpPoints;
                if (amount <= 0) return;

                string identity = IdentityOf(__instance);

                if (!string.IsNullOrEmpty(identity))
                {
                    _paid.Add(identity);
                }

                _shared++;
                CoopSession.ReportDialogueExp(new DialogueExpMessage { Amount = amount, Identity = identity });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] reporting a dialogue reward failed: " + e);
            }
        }

        /// <summary>
        /// Names a reward node so every machine names it the same. Node.ID is authored into the
        /// graph asset; Node.UID is not - the authored graphs carry UIDs for only some nodes and
        /// the rest are minted at runtime as fresh guids, different on every machine, which let
        /// the same reward through the dedup twice.
        /// </summary>
        private static string IdentityOf(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp node)
        {
            try
            {
                return (node.DLGTree?.name ?? "?") + "#" + node.ID;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
