using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.ActorActions;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Lets the player who was guarding decide whether their guard broke. A puppet's guard always holds
    /// locally, since it lacks the owner's live stamina and block talents, and a real break is played out later.
    /// </summary>
    internal static class BlockVerdicts
    {
        /// <summary>
        /// Swings struck and not yet answered, per victim. A verdict must spend one, so a duplicate or late
        /// verdict cannot floor a body.
        /// </summary>
        private static readonly Dictionary<PlayerId, List<uint>> Awaiting = new();

        /// <summary>How many unanswered swings are remembered per victim. Most are never answered.</summary>
        private const int MaxAwaiting = 24;

        /// <summary>Whether the puppet guard suppression installed.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>How many verdicts this machine answered a blow with.</summary>
        internal static int Sent { get; private set; }

        /// <summary>How many verdicts this machine played out on a puppet.</summary>
        internal static int Applied { get; private set; }

        /// <summary>How many local guard decisions were refused on a puppet.</summary>
        internal static int Suppressed { get; private set; }

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The private method that actually decides has a single caller and gets inlined, this entry point does not.
            Hooked = Hooking.TryPrefix(harmony, typeof(AA_BlockAction), nameof(AA_BlockAction.CalculateResultDamage),
                typeof(BlockVerdicts), nameof(CalculateResultDamagePrefix));

            if (!Hooked)
            {
                MelonLogger.Warning("[Block] a puppet's guard will decide for itself; verdicts will still cross.");
            }
        }

        /// <summary>Notes a blow this machine just struck, so its verdict can be recognized.</summary>
        internal static void Await(PlayerId victim, uint sequence)
        {
            if (!Awaiting.TryGetValue(victim, out List<uint>? pending))
            {
                pending = new List<uint>();
                Awaiting[victim] = pending;
            }

            pending.Add(sequence);

            if (pending.Count > MaxAwaiting) pending.RemoveAt(0);
        }

        private static bool Claim(PlayerId victim, uint sequence)
        {
            if (!Awaiting.TryGetValue(victim, out List<uint>? pending)) return false;

            return pending.Remove(sequence);
        }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            Awaiting.Clear();
            Sent = 0;
            Applied = 0;
            Suppressed = 0;
        }

        /// <summary>Reads the verdict off a blow this player just resolved, and tells the attacker.</summary>
        internal static void Answer(PlayerId attacker, uint sequence, Actor player, bool guarding, float landed)
        {
            if (!CoopSession.IsLive) return;

            try
            {
                byte verdict = Read(player, guarding, landed);
#if DEBUG
                MelonLogger.Msg($"[Block] blow {sequence} from {attacker}: guarding {guarding}, landed {landed:0}, verdict {verdict}.");
#endif

                // Only a break crosses, the one verdict the far side plays out.
                if (verdict != BlockVerdictMessage.VerdictBroken) return;

                CoopSession.ReportBlockVerdict(new BlockVerdictMessage
                {
                    Attacker = attacker,
                    Victim = CoopSession.LocalPlayerId,
                    Sequence = sequence,
                    Verdict = verdict,
                });

                Sent++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Block] answering a blow with a verdict failed: " + e);
            }
        }

        /// <summary>Plays a verdict out on the puppet of the player who was guarding.</summary>
        internal static void Apply(PlayerId victim, uint sequence, Actor body, byte verdict)
        {
            if (body == null) return;

            if (!Claim(victim, sequence)) return;

            try
            {
                if (verdict != BlockVerdictMessage.VerdictBroken) return;

                // The game's own guard break: zero poise, forced past immunities, drives every stagger behavior.
                Il2CppDrova.Poise.PoiseController poise = body.PoiseController;
                if (poise != null) poise.ChangePoise(0f - poise.CurValue, true);

                Il2CppDrova.StaminaSystem.StaminaController stamina = body.GetStaminaController();
                if (stamina != null) stamina.TriggerNotEnough();

                Applied++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Block] playing out a teammate's guard break failed: " + e);
            }
        }

        /// <summary>
        /// Classifies a resolved blow. A break is damage through, the guard gone, or poise at zero, since a
        /// broken guard can eat nothing.
        /// </summary>
        private static byte Read(Actor player, bool guarding, float landed)
        {
            if (!guarding) return BlockVerdictMessage.VerdictNone;

            AA_BlockAction? block = CurrentBlock(player);

            if (block != null && block.IsInPerfectBlockWindow()) return BlockVerdictMessage.VerdictPerfect;

            Il2CppDrova.Poise.PoiseController? poise = player.PoiseController;
            bool broken = landed > 0f || block == null || (poise != null && poise.CurValue <= 0f);
            return broken ? BlockVerdictMessage.VerdictBroken : BlockVerdictMessage.VerdictBlocked;
        }

        /// <summary>Whether this body has a guard up right now.</summary>
        internal static bool IsGuarding(Actor player) => CurrentBlock(player) != null;

        private static AA_BlockAction? CurrentBlock(Actor? actor)
        {
            if (actor == null) return null;

            Actor.ActionModule actions = actor.GetActionModule();
            if (actions == null || !actions.HasCurrentAction) return null;

            return actions.CurrentAction?.TryCast<AA_BlockAction>();
        }

        /// <summary>
        /// Makes a puppet's guard always hold. A wrong break cannot be taken back, a real one arrives as a verdict.
        /// </summary>
        private static bool CalculateResultDamagePrefix(AA_BlockAction __instance, ref float __result)
        {
            try
            {
                Actor actor = __instance._actor;
                if (actor == null || !RemotePlayers.IsPuppet(actor)) return true;

                // The blow itself is resolved on its owner's machine.
                __result = 0f;
                Suppressed++;
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Block] refusing a puppet's guard decision failed: " + e);
                return true;
            }
        }
    }
}
