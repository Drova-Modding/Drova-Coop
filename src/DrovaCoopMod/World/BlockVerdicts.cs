using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.ActorActions;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Lets the player who was actually guarding say whether their guard held.
    ///
    /// The blow crosses rather than the wound, so the defender's own armor and guard settle the damage
    /// where the numbers are real. The verdict is the half that never crossed: the attacker's machine
    /// works it out again, from a puppet, against a stamina pool mirrored one round trip ago. So the
    /// same swing can hold on one screen and break a guard on the other, and the two players watch a
    /// different fight.
    ///
    /// A puppet cannot even reach the right answer on its own. The perfect-block window is gated on the
    /// blocker having learned the talent for it, and a puppet's talents are not replicated, so the
    /// window is never so much as written on a remote body: every perfect block the far player lands
    /// reads as an ordinary one here, forever, whatever the stamina says.
    ///
    /// Two halves, and the first matters more than the second. A puppet's guard is stopped from breaking
    /// on its own - the recomputation is refused outright, and the body reads as holding until told
    /// otherwise - so the attacker never sees a stagger that did not happen. Then the real verdict
    /// arrives a round trip later and a guard break is played out for real, through the game's own
    /// zeroed poise, which is what every stagger behavior on the body listens to.
    ///
    /// The status effect a blocked swing lays on the attacker is applied by a behavior on the blocking
    /// body at the moment of the hit, before any verdict could arrive, so it is picked from whatever the
    /// window says here. That used to be permanently wrong and is not any more: the window is held open
    /// on a puppet for as long as its owner's is, from a clock already slid back by a round trip. See
    /// <c>RemotePlayer.HoldPerfectWindow</c>.
    /// </summary>
    internal static class BlockVerdicts
    {
        /// <summary>
        /// The blows this machine has struck and not yet had answered, per victim.
        ///
        /// A verdict is a stagger, and a stagger applied to the wrong moment is a body thrown to the
        /// floor for nothing. A duplicate or a late verdict from a relay hiccup would do exactly that, so
        /// one is only acted on when it names a swing this machine is still waiting on, and naming it
        /// spends it.
        /// </summary>
        private static readonly Dictionary<PlayerId, List<uint>> _awaiting = new();

        /// <summary>
        /// How many unanswered swings are remembered per victim. Most are never answered at all now that
        /// only a break crosses, so this is a window rather than a queue.
        /// </summary>
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
            // The blocker's own entry point, and an interface implementation, so it cannot have been
            // inlined away. The method that actually decides is private with a single caller, which is
            // the shape that disappears.
            Hooked = Hooking.TryPrefix(harmony, typeof(AA_BlockAction), nameof(AA_BlockAction.CalculateResultDamage),
                typeof(BlockVerdicts), nameof(CalculateResultDamagePrefix));

            if (!Hooked)
            {
                MelonLogger.Warning("[Block] a puppet's guard will decide for itself; verdicts will still cross.");
            }
        }

        /// <summary>
        /// Notes a blow this machine has just struck, so its answer can be recognised. Called where the
        /// report is stamped, because that is where the number is decided.
        /// </summary>
        internal static void Await(PlayerId victim, uint sequence)
        {
            if (!_awaiting.TryGetValue(victim, out List<uint>? pending))
            {
                pending = new List<uint>();
                _awaiting[victim] = pending;
            }

            pending.Add(sequence);

            // Oldest first: a swing that has gone this long unanswered held, or its answer was lost, and
            // either way nothing is owed to it any more.
            if (pending.Count > MaxAwaiting) pending.RemoveAt(0);
        }

        /// <summary>
        /// Whether a verdict names a swing still waiting for one, spending it if so.
        /// </summary>
        private static bool Claim(PlayerId victim, uint sequence)
        {
            if (!_awaiting.TryGetValue(victim, out List<uint>? pending)) return false;

            return pending.Remove(sequence);
        }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            _awaiting.Clear();
            Sent = 0;
            Applied = 0;
            Suppressed = 0;
        }

        /// <summary>
        /// Reads the verdict off a blow this player has just resolved, and tells the attacker.
        ///
        /// Called with the figures the victim's own health change produced, which is the only place both
        /// halves are known: how much the guard ate, and how much got through anyway.
        /// </summary>
        /// <param name="attacker">Who swung.</param>
        /// <param name="sequence">Which blow, as the report numbered it.</param>
        /// <param name="player">The body that was struck, for its live block action.</param>
        /// <param name="landed">What the blow actually took off, after everything.</param>
        /// <param name="blocked">What the guard ate.</param>
        internal static void Answer(PlayerId attacker, uint sequence, Actor player, float landed, float blocked)
        {
            if (!CoopSession.IsLive) return;

            try
            {
                byte verdict = Read(player, landed, blocked);

                // Only a break crosses. It is the one verdict the far side plays out, and every blow
                // that touched no guard at all would otherwise be answered with a packet saying so.
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

        /// <summary>
        /// Plays a verdict out on the body that was guarding, which on this machine is a puppet.
        /// </summary>
        /// <param name="victim">Who answered, which names the swing together with the sequence.</param>
        /// <param name="sequence">Which blow, as the report numbered it.</param>
        /// <param name="body">The puppet of the player who answered.</param>
        /// <param name="verdict">What their own game decided.</param>
        internal static void Apply(PlayerId victim, uint sequence, Actor body, byte verdict)
        {
            if (body == null) return;

            // Named by a swing this machine is still waiting on, or ignored. A verdict that arrives twice
            // or long after the fact would otherwise floor a body that is no longer even guarding.
            if (!Claim(victim, sequence)) return;

            try
            {
                // Only a break is played out. A guard that held is what the puppet is already showing,
                // because its own decision was refused in favour of holding, and a perfect block reaches
                // this machine too late for the flash and the spark that would have marked it.
                if (verdict != BlockVerdictMessage.VerdictBroken) return;

                // The game's own guard break: poise driven to nothing, forced past the immunities,
                // which is what every stagger behavior on the body is listening for. The stagger, the
                // stun and the animation all follow from it rather than being imitated here.
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
        /// Which of the four a resolved blow was.
        ///
        /// The guard ate nothing at all means it was not a block. Otherwise the perfect window decides,
        /// and failing that whether anything got through: a guard that held leaks nothing, and a guard
        /// that broke leaks the part of the blow its stamina could not pay for.
        /// </summary>
        private static byte Read(Actor player, float landed, float blocked)
        {
            if (blocked <= 0f) return BlockVerdictMessage.VerdictNone;

            AA_BlockAction? block = CurrentBlock(player);

            if (block != null && block.IsInPerfectBlockWindow()) return BlockVerdictMessage.VerdictPerfect;

            return landed > 0f ? BlockVerdictMessage.VerdictBroken : BlockVerdictMessage.VerdictBlocked;
        }

        private static AA_BlockAction? CurrentBlock(Actor? actor)
        {
            if (actor == null) return null;

            Actor.ActionModule actions = actor.GetActionModule();
            if (actions == null || !actions.HasCurrentAction) return null;

            return actions.CurrentAction?.TryCast<AA_BlockAction>();
        }

        /// <summary>
        /// Stops a puppet's guard deciding its own fate.
        ///
        /// The body would otherwise answer out of a stamina pool that is a round trip old and a perfect
        /// window it can never enter, and the answer it reaches is the one the attacker watches. Held is
        /// the safe provisional reading: a break that turns out not to have happened cannot be taken
        /// back, while one that did is played out a round trip later from the owner's own verdict.
        /// </summary>
        private static bool CalculateResultDamagePrefix(AA_BlockAction __instance, ref float __result)
        {
            try
            {
                Actor actor = __instance._actor;
                if (actor == null || !RemotePlayers.IsPuppet(actor)) return true;

                // Zero rather than the incoming figure: the blow itself is replicated to its owner and
                // resolved there, so what this body does with it changes nothing but what is shown.
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
