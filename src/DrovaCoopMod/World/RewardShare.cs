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
        /// Reward nodes already paid here, by dialogue tree plus node id, for this session and written
        /// into the savegame so it outlives a reload. Both players sitting through the same
        /// conversation is one reward, not two: the second local grant is zeroed and the second
        /// arriving grant is dropped. See <see cref="GrantLedger"/>.
        /// </summary>
        private static readonly GrantLedger _ledger = new("CoopPaid_");

        /// <summary>How many rewards this savegame has recorded. For <c>coop_status</c>.</summary>
        internal static int Remembered => _ledger.Remembered;

        /// <summary>How many repeats the savegame's own memory caught. For <c>coop_status</c>.</summary>
        internal static int RememberedHits => _ledger.Hits;

        /// <summary>How many notes the savegame refused. Above zero means a repeat is still possible.</summary>
        internal static int RefusedWrites => _ledger.RefusedWrites;

        /// <summary>How many grants this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared { get; private set; }

        /// <summary>How many grants arrived and were paid here. For <c>coop_status</c>.</summary>
        internal static int Received { get; private set; }

        /// <summary>Forgets the counts and what was paid. Called when a session ends.</summary>
        internal static void Reset()
        {
            _ledger.Reset();
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

                // Already paid, by this player sitting through the conversation themselves or by an
                // earlier copy of this grant. An identityless grant cannot be deduplicated and is
                // paid as it comes.
                if (!string.IsNullOrEmpty(message.Identity))
                {
                    // This session, and silently: two copies of one conversation are the ordinary case.
                    if (_ledger.IsKnown(message.Identity)) return;

                    // An earlier session. Worth a line, because it is the only evidence that the note
                    // survived a reload - which is the whole of what this remembers for.
                    if (_ledger.WasPaid(message.Identity))
                    {
                        _ledger.Claim(message.Identity);

                        MelonLogger.Msg("[Exp] a dialogue reward this savegame already collected was " +
                            "skipped; it was paid in an earlier session and would otherwise pay twice.");

                        return;
                    }

                    _ledger.Claim(message.Identity);
                }

                if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

                PlayerAttributeStats? stats = player.GetStats()?.TryCast<PlayerAttributeStats>();

                if (stats == null) return;

                stats.AddExperiencePoints(message.Amount);
                Received++;

                // After the payment, never before it: a reward remembered on a frame where the player's
                // stats could not be reached is experience nobody ever gets.
                _ledger.Remember(message.Identity);

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
                if (!CoopSession.IsLive) return;
                if (__instance == null) return;

                string identity = GrantLedger.IdentityOf(__instance);
                if (string.IsNullOrEmpty(identity)) return;

                bool thisSession = _ledger.IsKnown(identity);
                if (!thisSession && !_ledger.WasPaid(identity)) return;

                __state = __instance.ExpPoints;
                __instance.ExpPoints = 0;

                // Which memory caught it: only the savegame one proves the note survived a reload.
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

                // The prefix zeroed a repeat: put the authored figure back and report nothing.
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
                    // The local player has been paid by the game's own node here, so this one is safe
                    // to write down. An identityless grant has nothing to write and stays repeatable.
                    _ledger.Claim(identity);
                    _ledger.Remember(identity);
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
