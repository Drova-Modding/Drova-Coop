using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Pays everybody the experience a dialogue reward node granted one player.
    /// </summary>
    internal static class RewardShare
    {
        /// <summary>
        /// Reward nodes already paid, saved so it outlives a reload. One conversation pays once even
        /// when both players sit through it.
        /// </summary>
        private static readonly GrantLedger Ledger = new("CoopPaid_");

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

        /// <summary>Forgets the counts and what was paid. Called when a session ends.</summary>
        internal static void Reset()
        {
            Ledger.Reset();
            Shared = 0;
            Received = 0;
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
            if (!CoopSession.IsLive)
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

                // An identityless grant cannot be deduplicated and is paid as it comes.
                if (!string.IsNullOrEmpty(message.Identity))
                {
                    if (Ledger.IsKnown(message.Identity)) return;

                    if (Ledger.WasPaid(message.Identity))
                    {
                        Ledger.Claim(message.Identity);

                        MelonLogger.Msg("[Exp] a dialogue reward this savegame already collected was " +
                            "skipped; it was paid in an earlier session and would otherwise pay twice.");

                        return;
                    }

                    Ledger.Claim(message.Identity);
                }

                if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

                PlayerAttributeStats? stats = player.GetStats()?.TryCast<PlayerAttributeStats>();

                if (stats == null) return;

                stats.AddExperiencePoints(message.Amount);
                Received++;

                // Remembered only after paying, or a failed payment would be lost for good.
                Ledger.Remember(message.Identity);

                MelonLogger.Msg($"[Exp] {message.Amount} experience arrived from a teammate's dialogue.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] paying a teammate's dialogue reward failed: " + e);
            }
        }

        /// <summary>
        /// Zeroes a reward already collected so the dialogue still continues. The postfix restores the amount.
        /// </summary>
        private static void GiveExpPrefix(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp __instance,
            out int __state)
        {
            __state = 0;

            try
            {
                if (!CoopSession.IsLive) return;
                if (__instance == null) return;

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

        private static void GiveExpPostfix(Il2CppNodeCanvas.DialogueTrees.DS_GiveExp __instance, int __state)
        {
            try
            {
                if (__instance == null) return;

                // The prefix zeroed a repeat: restore the authored figure and report nothing.
                if (__state > 0)
                {
                    __instance.ExpPoints = __state;
                    return;
                }

                if (!CoopSession.IsLive) return;

                int amount = __instance.ExpPoints;
                if (amount <= 0) return;

                string identity = GrantLedger.IdentityOf(__instance);

                if (!string.IsNullOrEmpty(identity))
                {
                    Ledger.Claim(identity);
                    Ledger.Remember(identity);
                }

                Shared++;
                CoopSession.ReportDialogueExp(new DialogueExpMessage { Amount = amount, Identity = identity });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Exp] reporting a dialogue reward failed: " + e);
            }
        }
    }
}
