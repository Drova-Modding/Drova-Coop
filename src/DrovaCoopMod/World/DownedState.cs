using Drova_Modding_API.Access;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.ABI;
using Il2CppDrova.Brawl;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Puts a player who would have died on the floor instead, and gets them back up.
    ///
    /// Dying is not recoverable and cannot be undone afterwards. <c>Health.CheckEvents</c> latches
    /// <c>_isDead</c> the moment health reaches zero, and nothing in the game ever assigns false to it -
    /// there is no such assignment anywhere in the assembly. After that <c>ChangeHealth</c> refuses
    /// outright, so the actor can be healed to full and stay dead. Anything that looks like recovery in
    /// Drova is therefore prevention, applied before the blow lands.
    ///
    /// The game's own prevention is <c>ReviveBlocker</c>, a damage blocker that watches for a hit which
    /// would take its owner to one health or less, sets them to two, raises an event and reports the
    /// damage as zero. Health never reaches the floor, the flag never latches, and the death screen -
    /// which hangs off <c>EntityGameHandler.PlayerActorDiedEvent</c> - is never reached, so there is
    /// nothing to suppress.
    ///
    /// Getting up is faster with somebody standing over you, which is the part that makes this a
    /// co-op mechanic rather than a delay. The clock is the fallback, not the intent.
    ///
    /// What the player sees is the game's own knockout, not an approximation of one:
    /// <c>BrawlActor.Knockout</c> blocks input through the player controller, stops the AI, clears the
    /// queued actions, moves the interaction module into <c>KnockOut</c>, and takes down both the body
    /// collision and the hit receiver. That is the same state an NPC is left in by a lost brawl.
    /// </summary>
    internal static class DownedState
    {
        /// <summary>
        /// How long a downed player stays down before getting up on their own.
        ///
        /// Matches the knockout the game authors for its own brawls, which is the point: a co-op player
        /// on the floor should read as the state Drova already has a language for rather than as a new
        /// one. Long enough to be a real setback in a fight, short enough that the other player is not
        /// left alone with it.
        /// </summary>
        private const float DownedSeconds = 5f;

        /// <summary>
        /// How close another player has to stand to be helping rather than passing by.
        ///
        /// In world units, and deliberately close enough that it reads as standing over somebody. The
        /// range is not checked against a facing or an interaction prompt, so the only thing keeping it
        /// from happening by accident is how small it is.
        /// </summary>
        private const float HelpUpRange = 2.5f;

        /// <summary>
        /// How long another player has to stand there before the downed one gets up.
        ///
        /// Shorter than <see cref="DownedSeconds"/> by enough to be worth crossing a fight for, long
        /// enough that running past somebody does not do it.
        /// </summary>
        private const float HelpUpSeconds = 1.5f;

        // Held in fields rather than rebuilt, because the blocker is matched by reference: the game
        // removes one by identity, and a second instance would leave the first installed forever.
        private static ReviveBlocker? _blocker;
        private static BrawlActor? _downed;
        private static Health? _guarded;
        private static float _downedFor;
        private static float _helpedFor;

        /// <summary>Whether the local player is currently on the floor.</summary>
        internal static bool IsDown => _downed != null;

        /// <summary>
        /// Installs the guard while a session is live, removes it when one ends, and counts a downed
        /// player back up. Call once per frame.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame, unaffected by
        /// <c>Time.timeScale</c>, so a window that stops the clock cannot hold a player down forever.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Uninstall();
                return;
            }

            if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return;

            Install(player);
            CountUp(unscaledDeltaTime);
        }

        /// <summary>
        /// Takes the guard off and stands the player up, so leaving a session cannot strand them.
        /// </summary>
        internal static void Uninstall()
        {
            if (_downed != null)
            {
                StandUp();
            }

            if (_blocker != null && _guarded != null)
            {
                _guarded.RemoveDamageBlocker(_blocker.Cast<IDamageBlocker>());
                MelonLogger.Msg("[Downed] guard removed; death is fatal again.");
            }

            _blocker = null;
            _guarded = null;
        }

        /// <summary>
        /// Adds the blocker to whoever is the local player now.
        ///
        /// Re-checked rather than done once, because the player actor is replaced when a save is loaded
        /// and the blocker lives on the old one's health component. Comparing the component rather than
        /// counting on an event means a swap cannot be missed.
        /// </summary>
        private static void Install(Actor player)
        {
            Health health = player.GetHealth();
            if (health == null) return;

            // By pointer: two proxies over one native component are different managed objects.
            if (_guarded != null && _guarded.Pointer == health.Pointer) return;

            _blocker = new ReviveBlocker();
            _guarded = health;

            // The event is the only signal that the blocker fired. It carries the entity it saved, which
            // is this player, and it is raised from inside the damage calculation - so the knockout is
            // requested one frame's worth of game code deep in whatever swing caused it. That is where
            // the game raises its own, too.
            _blocker.ReviveEvent.AddEventListener(new Action<Entity>(GoDown));

            health.AddDamageBlocker(_blocker.Cast<IDamageBlocker>());

            MelonLogger.Msg("[Downed] guard installed; a killing blow will put this player down instead.");
        }

        /// <summary>
        /// Puts the player on the floor.
        /// </summary>
        private static void GoDown(Entity saved)
        {
            try
            {
                if (_downed != null) return;

                Actor? actor = saved?.TryCast<Actor>();
                if (actor == null) return;

                BrawlActor brawler = new(actor);

                // Dialogue first, because being knocked out of a conversation leaves the window up over a
                // player who can no longer answer it.
                actor.DialogueModule.StopDialogue();

                brawler.Knockout();

                _downed = brawler;
                _downedFor = 0f;
                _helpedFor = 0f;

                MelonLogger.Msg($"[Downed] this player is down; getting up in {DownedSeconds:0.0}s, " +
                    $"or in {HelpUpSeconds:0.0}s if somebody stands over them.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Downed] going down failed: " + e);
            }
        }

        /// <summary>
        /// Counts a downed player back up, and lets the game or another player cut that short.
        /// </summary>
        private static void CountUp(float unscaledDeltaTime)
        {
            if (_downed == null) return;

            _downedFor += unscaledDeltaTime;

            // The game's own early-out as well as the clock. ForceEndKnockout is what a help-up
            // interaction sets, so honoring it is what would let the other player shorten this rather
            // than only watch it.
            bool forced = _downed.GetActor() != null && _downed.GetActor().CustomStats.ForceEndKnockout;
            bool helped = IsBeingHelped(unscaledDeltaTime);

            if (_downedFor < DownedSeconds && !forced && !helped) return;

            StandUp(helped);
        }

        /// <summary>
        /// Whether another player has stood over this one long enough to pull them up.
        ///
        /// Decided by the player on the floor, not by the one helping. Both machines know where every
        /// body is, so the only side that has to be convinced is the one that acts on it - and asking here
        /// means no message, no protocol version, and no way for the two to disagree about whether a
        /// revive happened. What the helper gives up is a prompt: from their side this is standing next to
        /// somebody, not pressing a button.
        ///
        /// A body that is itself knocked out does not count. Two players on the floor beside each other
        /// would otherwise haul each other up at once, which turns going down into nothing at all as long
        /// as it happens to both.
        /// </summary>
        private static bool IsBeingHelped(float unscaledDeltaTime)
        {
            Actor? actor = _downed?.GetActor();
            if (actor == null) return false;

            if (!AnyoneStandingOver(actor))
            {
                // Reset rather than kept, so helping has to be continuous. Otherwise a player who ran past
                // twice would have banked the time as if they had stayed.
                _helpedFor = 0f;
                return false;
            }

            _helpedFor += unscaledDeltaTime;

            return _helpedFor >= HelpUpSeconds;
        }

        /// <summary>
        /// Whether any remote player's body is close enough, and upright enough, to be helping.
        /// </summary>
        private static bool AnyoneStandingOver(Actor actor)
        {
            Vector3 down = actor.transform.position;

            foreach (RemotePlayer other in RemotePlayers.Bodies.Values)
            {
                Actor body = other.Body;
                if (body == null) continue;

                if (body.CustomStats != null && body.CustomStats.IsKnockout) continue;

                // Squared, so nothing has to take a square root thirty times a second to answer a question
                // that only cares which side of a line the body is on.
                if ((body.transform.position - down).sqrMagnitude <= HelpUpRange * HelpUpRange) return true;
            }

            return false;
        }

        /// <summary>
        /// Stands the player back up.
        /// </summary>
        /// <param name="helped">Whether another player pulled them up rather than the clock running out.
        /// Only changes what is said about it; both routes recover the same way.</param>
        private static void StandUp(bool helped = false)
        {
            try
            {
                if (_downed == null) return;

                Actor actor = _downed.GetActor();
                if (actor != null)
                {
                    actor.CustomStats.ForceEndKnockout = false;
                }

                _downed.RemoveKnockout();

                // Both, in the order the game itself uses. HealthBhvr_ReviveBhvr.Revive - Drova's own
                // downed-and-recover behavior, which this class mirrors step for step without having
                // been written from it - calls RemoveKnockout, then RequestHeal, then a health write.
                //
                // RequestHeal alone does nothing for a player, and believing otherwise cost a session.
                // It sets `CustomStats.RequestHeal`, and the only thing in the whole assembly that reads
                // that flag is `RequestedHealCondition`, a NodeCanvas condition inside an AI graph. A
                // player has no such graph. The flag was set and never consumed, so the player stood up
                // on the two health the revive blocker had left them - one hit from being down again. A
                // twenty-minute log reported 2/40 from start to finish.
                //
                // Kept anyway, because it is what the game does and it costs nothing; the write below is
                // what actually heals.
                _downed.RequestHeal();
                HealForRecovery(actor);

                _downed = null;
                _downedFor = 0f;
                _helpedFor = 0f;

                MelonLogger.Msg(helped
                    ? "[Downed] another player stood over this one and pulled them up."
                    : "[Downed] this player is back up.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Downed] getting up failed: " + e);
                _downed = null;
            }
        }

        /// <summary>
        /// Puts the session's recovery share of health back on a player who has just stood up.
        ///
        /// A fraction of the maximum rather than a figure, because characters differ and what matters is
        /// how much of a fight is left. The share is the host's - see
        /// <see cref="CoopSession.RecoveryHealthPercent"/> - so both players pay the same price for being
        /// put down.
        ///
        /// Written with <c>SetHealth</c> rather than <c>SetHealthPercent</c> and only upwards. The two
        /// differ when a player is already above the share: the percent form would take health away, and
        /// standing up is not supposed to be able to hurt.
        /// </summary>
        private static void HealForRecovery(Actor? actor)
        {
            if (actor == null) return;

            Health health = actor.GetHealth();
            if (health == null) return;

            int wanted = Mathf.RoundToInt(health.MaxHealth * (CoopSession.RecoveryHealthPercent / 100f));
            if (wanted < 1) wanted = 1;
            if (wanted <= health.CurHealth) return;

            health.SetHealth(wanted, null);
        }
    }
}
