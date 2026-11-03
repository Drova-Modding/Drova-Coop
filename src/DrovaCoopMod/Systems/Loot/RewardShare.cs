using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Loot.Patches;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppNodeCanvas.DialogueTrees;
using MelonLoader;

namespace DrovaCoopMod.Systems.Loot
{
    /// <summary>
    /// Pays everybody the experience a dialogue reward node granted one player.
    /// </summary>
    internal sealed class RewardShare : ICoopSystem
    {
        /// <summary>
        /// Reward nodes already paid, saved so it outlives a reload. One conversation pays once even
        /// when both players sit through it.
        /// </summary>
        private static readonly GrantLedger Ledger = new("CoopPaid_");

        private static readonly Queue<DialogueExpMessage> IncomingDialogueExp = new();

        /// <summary>How many rewards this savegame has recorded. For <c>coop_status</c>.</summary>
        internal static int Remembered => Ledger.Remembered;

        /// <summary>How many repeats the savegame's own memory caught. For <c>coop_status</c>.</summary>
        internal static int RememberedHits => Ledger.Hits;

        /// <summary>How many notes the savegame refused. Above zero means a repeat is still possible.</summary>
        internal static int RefusedWrites => Ledger.RefusedWrites;

        /// <summary>How many grants this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared { get; private set; }

        /// <summary>How many grants arrived and were paid here. For <c>coop_status</c>.</summary>
        internal static int Received { get; private set; }

        /// <summary>How many reports named no reward node loaded here and were refused.</summary>
        internal static int Unresolved { get; private set; }

        /// <summary>Forgets the counts and what was paid.</summary>
        private static void Reset()
        {
            Ledger.Reset();
            Shared = 0;
            Received = 0;
        }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp), "OnExecute",
                typeof(RewardSharePatches), nameof(RewardSharePatches.GiveExpPrefix));
            Hooking.TryPostfix(harmony, typeof(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp), "OnExecute",
                typeof(RewardSharePatches), nameof(RewardSharePatches.GiveExpPostfix));
        }

        /// <summary>Pays everything that has arrived.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
            {
                Reset();
                return;
            }

            while (TryTakeDialogueExp(out DialogueExpMessage message))
            {
                Apply(message);
            }
        }

        private static void Apply(in DialogueExpMessage message)
        {
            try
            {
                if (!message.IsPlausible()) return;

                // An identityless grant cannot be deduplicated.
                if (string.IsNullOrEmpty(message.Identity)) return;
                if (Ledger.IsKnown(message.Identity)) return;

                // Before the savegame ledger, so a made-up identity never gets written there.
                DS_GiveExp? node = GrantLedger.FindNode<DS_GiveExp>(message.Identity);
                int amount = node == null ? 0 : Math.Min(node.ExpPoints, message.Amount);
                if (amount <= 0)
                {
                    Unresolved++;
                    if (Unresolved == 1)
                    {
                        MelonLogger.Warning($"[Exp] a teammate's dialogue reward '{message.Identity}' names no reward node loaded here and was refused.");
                    }

                    return;
                }

                if (Ledger.WasPaid(message.Identity))
                {
                    Ledger.Claim(message.Identity);

                    MelonLogger.Msg("[Exp] a dialogue reward this savegame already collected was " +
                        "skipped; it was paid in an earlier session and would otherwise pay twice.");

                    return;
                }

                Ledger.Claim(message.Identity);

                if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

                AttributeStats? attributes = player.GetStats();
                PlayerAttributeStats? stats = attributes == null ? null : attributes.TryCast<PlayerAttributeStats>();
                if (stats == null) return;

                stats.AddExperiencePoints(amount);
                Received++;

                // Remembered only after paying, or a failed payment would be lost for good.
                Ledger.Remember(message.Identity);

                MelonLogger.Msg($"[Exp] {amount} experience arrived from a teammate's dialogue.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] paying a teammate's dialogue reward failed: " + e);
            }
        }

        /// <summary>
        /// Zeroes a reward already collected so the dialogue still continues. The postfix restores the amount.
        /// </summary>
        internal static void GiveExpPrefix(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp __instance,
            out int __state)
        {
            __state = 0;

            try
            {
                if (!CoopSession.SharesWorld) return;

                string identity = GrantLedger.IdentityOf(__instance);
                if (string.IsNullOrEmpty(identity)) return;

                bool thisSession = Ledger.IsKnown(identity);
                if (!thisSession && !Ledger.WasPaid(identity)) return;

                __state = __instance.ExpPoints;
                __instance.ExpPoints = 0;

                MelonLogger.Msg(thisSession
                    ? "[Exp] a dialogue reward this world already collected was skipped, because a " +
                      "teammate's copy of the same conversation paid it first."
                    : "[Exp] a dialogue reward this savegame already collected was skipped; it was " +
                      "paid in an earlier session and would otherwise pay a second time.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] refusing a repeated dialogue reward failed: " + e);
            }
        }

        internal static void GiveExpPostfix(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp __instance, int __state)
        {
            try
            {
                // The prefix zeroed a repeat. Restore the authored figure and report nothing.
                if (__state > 0)
                {
                    __instance.ExpPoints = __state;
                    return;
                }

                if (!CoopSession.SharesWorld) return;

                int amount = __instance.ExpPoints;
                if (amount <= 0) return;

                string identity = GrantLedger.IdentityOf(__instance);

                // Teammates refuse a reward they cannot trace to its node.
                if (string.IsNullOrEmpty(identity)) return;

                Ledger.Claim(identity);
                Ledger.Remember(identity);

                Shared++;
                ReportDialogueExp(new DialogueExpMessage { Amount = amount, Identity = identity });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] reporting a dialogue reward failed: " + e);
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            // Never back to the sender, since a payment is not idempotent.
            CoopSession.Register<DialogueExpMessage>(MessageIds.DialogueExp,
                (peer, m) => CoopSession.RelayAndQueue(peer, m, IncomingDialogueExp, CoopSession.MaxPendingFromHost, "dialogue rewards"),
                worldOnly: true);
        }

        void ICoopSystem.EndSession()
        {
            IncomingDialogueExp.Clear();
        }

        /// <summary>Takes the next dialogue reward that arrived.</summary>
        private static bool TryTakeDialogueExp(out DialogueExpMessage message)
            => IncomingDialogueExp.TryDequeue(out message);

        /// <summary>Reports a dialogue paying this player. The host pays and passes it on, sender excluded.</summary>
        private static void ReportDialogueExp(in DialogueExpMessage message)
            => CoopSession.SendIfLive(message);

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
