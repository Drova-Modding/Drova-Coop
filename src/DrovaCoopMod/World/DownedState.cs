using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
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
        private const float HelpUpRange = 40f;

        /// <summary>
        /// Hardcore: how long a downed player has before they die, unless a teammate gets them up. A death there is the
        /// end of every hero in the party (the mode deletes them), so a fall is a race, not a pause.
        /// </summary>
        private const float BleedOutSeconds = 30f;

        private static float _lastWarned;

        // Teammates seen bleeding out, so each fall is called out once.
        private static readonly HashSet<PlayerId> _bleeding = new();

        /// <summary>Set while a teammate's own blow is being applied here (RemoteDamage). Read when it puts the player down.</summary>
        internal static bool TeammateBlow;

        // Whether the current fall was a teammate's doing: friendly fire is a quarrel, not a death - it never bleeds out.
        private static bool _downedByTeammate;

        // Down in a game-arena fight: decided as the player goes down, not every frame - a fight ending in the seconds
        // the player lies there must not turn its knockout into a Hardcore bleed-out.
        private static bool _downedInArenaFight;

        /// <summary>Hardcore deaths by bleeding out on this machine. For tests.</summary>
        internal static int BledOut { get; private set; }

        /// <summary>
        /// How long another player has to stand there before the downed one gets up.
        ///
        /// Shorter than <see cref="DownedSeconds"/> by enough to be worth crossing a fight for, long
        /// enough that running past somebody does not do it.
        /// </summary>
        private const float HelpUpSeconds = 1.5f;

        /// <summary>
        /// How long a player who has just stood up cannot be hit.
        ///
        /// They stand up on the recovery share of their health, and whatever put them down is usually still
        /// swinging: without a moment's cover they were back on the floor within a second, which read as
        /// one blow knocking them down twice.
        /// </summary>
        private const float StandUpGraceSeconds = 2f;

        // Held in fields rather than rebuilt, because the blocker is matched by reference: the game
        // removes one by identity, and a second instance would leave the first installed forever.
        private static ReviveBlocker? _blocker;
        private static BrawlActor? _downed;
        private static Health? _guarded;
        private static float _downedFor;
        private static float _helpedFor;
        private static Actor? _graced;
        private static float _graceLeft;
        private static float _sinceStandCheck = 1f;
        private static float _sinceInstall = 5f;

        /// <summary>Whether the local player has just stood up and cannot be hit yet.</summary>
        internal static bool IsGraced => _graced != null;

        /// <summary>How many times a player still lying after getting up was stood up again. For tests.</summary>
        internal static int StandRetries { get; private set; }

        /// <summary>
        /// On the floor from a blow that would have killed them, and not a teammate's: what counts toward a lost run and a
        /// bleed-out. A friendly-fire fall is a quarrel.
        /// </summary>
        internal static bool IsLethallyDown => _downed != null && !_downedByTeammate;

        /// <summary>Whether the local player is currently on the floor.</summary>
        internal static bool IsDown => _downed != null;

        /// <summary>How often the guard was asked during the health change being applied now.</summary>
        private static int _asked;

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(ReviveBlocker), nameof(ReviveBlocker.CalculateResultDamage), typeof(DownedState), nameof(FirstPassPrefix));
            Hooking.TryPrefix(harmony, typeof(Health), nameof(Health.ChangeHealth), typeof(DownedState), nameof(ChangeStartsPrefix));
        }

        /// <summary>
        /// Starts the count for one health change on the guarded body. Counted per call rather than per
        /// change object: a change built for one blow can be freed and the next one built at the same address,
        /// and a count keyed on the address carried over - the early and late questions swapped, and a blow
        /// the player would have stood through put them down.
        /// </summary>
        private static void ChangeStartsPrefix(Health __instance)
        {
            if (_guarded != null && __instance != null && __instance.Pointer == _guarded.Pointer) _asked = 0;
        }

        /// <summary>
        /// Lets this player's guard judge a blow only once armor and shield have had their say.
        ///
        /// The game asks a revive blocker twice per damage value: among the other blockers, with the value as
        /// it arrived, and once more at the end with what is left of it. Asked first, it saw a sword's 44
        /// against 40 health and put the player down - behind a shield that then blocked the whole blow, and
        /// through armor that would have left them standing (combat_ff.coop). Only the last question is
        /// answered here.
        ///
        /// Told apart by counting, because the pass that asks last is inlined and cannot be hooked. The first
        /// question is only asked of a blockable blow, and then always before the last one for the same
        /// value: so for a blockable blow the odd questions are the early ones, and an unblockable blow - a
        /// fall - only ever asks the last one.
        /// </summary>
        private static bool FirstPassPrefix(ReviveBlocker __instance, HealthChange healthChange, float dmgValue, ref float __result)
        {
            if (_blocker == null || __instance == null || healthChange == null || __instance.Pointer != _blocker.Pointer) return true;

            // An invincible player (the invincibility cheat) takes no damage at all - the game says so after the blockers
            // have run. Left to judge the blow first, this guard put an invincible player on the floor anyway, and two of
            // them down is a lost run.
            if (_guarded != null && _guarded.CheatInvincible)
            {
                __result = dmgValue;
                return false;
            }

            _asked++;

            DmgSourceType? source = healthChange._dmgSourceType;
            bool blockable = source == null || source.IsBlockable;
            if (!blockable || _asked % 2 == 0) return true;

            __result = dmgValue;
            return false;
        }

        /// <summary>
        /// Installs the guard while a session is live, removes it when one ends, and counts a downed
        /// player back up. Call once per frame.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame, unaffected by
        /// <c>Time.timeScale</c>, so a window that stops the clock cannot hold a player down forever.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Uninstall();
                return;
            }

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            // A Hardcore death is final: the guard is not put back on a body that bled out.
            if (player.IsDead()) return;

            Install(player);
            CountUp(unscaledDeltaTime);
            WatchTeammates();
            CountGrace(unscaledDeltaTime);

            // Lying with nothing holding them there: no knockout of the game's, none of this mod's, no story hold. A save
            // written while a player was on the floor loads that way and nothing ever stands them up (tusk_bothdown.coop).
            // Only in the first seconds of a new body - what a load builds - and once a second there: the stance request
            // lands a few frames later. Later on, a knockdown effect that sets the mode alone (a hit's knockout) is the
            // game's own and must not be cut short.
            _sinceStandCheck += unscaledDeltaTime;
            _sinceInstall += unscaledDeltaTime;
            if (_sinceInstall < 5f && _sinceStandCheck >= 1f && _downed == null && _graced == null && player.CustomStats is { IsKnockout: false, KeepKnockoutState: false }
                && player.GetInteractionModule() is { CurrentMode: Actor.EInteractionMode.KnockOut } stance)
            {
                stance.RestrictQueue(false);
                stance.RequestMode(Actor.EInteractionMode.Normal);
                StandRetries++;
                _sinceStandCheck = 0f;
                MelonLogger.Msg("[Downed] this player was lying with no knockout behind it; stood them up.");
            }
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

            EndGrace();

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
            _sinceInstall = 0f;

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
                // The conversation runs on the NPC's controller, not on the player's own module: stopped there, the
                // way the game ends one, so "the player is in a dialogue" clears too. Left set, the dialogue system
                // kept the camera, and camera-loaded volumes near the player (exits, teleporters) stayed off.
                if (Il2CppDrova.DialogueNew.DialogueSystemGameHandler.TryGet(out Il2CppDrova.DialogueNew.DialogueSystemGameHandler dialogues)
                    && dialogues?.ActivePlayerDialogue != null && dialogues.ActivePlayerDialogue.isRunning)
                {
                    dialogues.ActivePlayerDialogue.StopDialogue();
                }
                else
                {
                    actor.DialogueModule.StopDialogue();
                }

                if (CameraGameHandler.TryGet(out CameraGameHandler camera) && camera != null) camera.ResetTemporaryCamTarget();

                brawler.Knockout();

                _downed = brawler;
                _downedByTeammate = TeammateBlow;
                _downedInArenaFight = ArenaGates.LocalInAFight();
                _downedFor = 0f;
                _lastWarned = -1f;
                _helpedFor = 0f;

                MelonLogger.Msg(Checkpoint.Mode == GameMode.Hardcore && !Arena.HoldsLocal() && !_downedInArenaFight && !_downedByTeammate
                    ? $"[Downed] this player is down and bleeding out: {BleedOutSeconds:0}s for somebody to stand over them."
                    : $"[Downed] this player is down; getting up in {DownedSeconds:0.0}s, or in {HelpUpSeconds:0.0}s if somebody stands over them.");
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

            // In an arena round a downed fighter stays down until the round is decided; its end stands them up.
            if (Arena.KeepsDown()) return;

            _downedFor += unscaledDeltaTime;

            // The game's own early-out as well as the clock. ForceEndKnockout is what a help-up
            // interaction sets, so honoring it is what would let the other player shorten this rather
            // than only watch it.
            bool forced = _downed.GetActor() != null && _downed.GetActor().CustomStats.ForceEndKnockout;
            bool helped = IsBeingHelped(unscaledDeltaTime);

            if (forced || helped)
            {
                StandUp(helped);
                return;
            }

            // Hardcore, outside an arena (ours, or a game arena fight): no getting up alone. A teammate has the time left to reach this player.
            if (Checkpoint.Mode == GameMode.Hardcore && !Arena.HoldsLocal() && !_downedInArenaFight && !_downedByTeammate)
            {
                BleedOut();
                return;
            }

            if (_downedFor < DownedSeconds) return;

            StandUp();
        }

        private static void BleedOut()
        {
            float left = BleedOutSeconds - _downedFor;
            if (left > 0f)
            {
                // Said at the fall and then every ten seconds, with the last five counted.
                float mark = left > 5f ? Mathf.Ceil(left / 10f) * 10f : Mathf.Ceil(left);
                if (mark != _lastWarned)
                {
                    _lastWarned = mark;
                    Announce($"You are bleeding out - {Mathf.CeilToInt(left)} s. A teammate must get you up.");
                }

                return;
            }

            // Too late: a real death. The guard steps aside; the game's own death follows, and the host ends the run for
            // the party (GameOver).
            Actor? actor = _downed?.GetActor();
            if (actor == null) return;

            MelonLogger.Msg("[Downed] Hardcore: nobody came in time; this player bled out.");
            BledOut++;
            Health health = actor.GetHealth();
            if (_blocker != null && health != null) health.RemoveDamageBlocker(_blocker.Cast<IDamageBlocker>());
            _blocker = null;
            _guarded = null;
            _downed = null;

            // Written, not dealt: a knocked-out body takes no blows (a pure-damage hit left the player standing at the same
            // health, and the guard went straight back on). Health at zero latches the game's own death.
            health?.SetHealth(0, null);
        }

        /// <summary>Hardcore: calls out a teammate who went down, so the others know the clock is running.</summary>
        private static void WatchTeammates()
        {
            if (Checkpoint.Mode != GameMode.Hardcore)
            {
                _bleeding.Clear();
                return;
            }

            foreach ((PlayerId id, RemotePlayer body) in RemotePlayers.Bodies)
            {
                bool bleeding = body != null && body.IsOut && !body.IsDead && !Arena.Holds(id);
                if (!bleeding)
                {
                    _bleeding.Remove(id);
                    continue;
                }

                if (_bleeding.Add(id))
                {
                    string name = CoopSession.Players.TryGetValue(id, out string? known) ? known : "A teammate";
                    Announce($"{name} is bleeding out! Stand over them within {BleedOutSeconds:0} s or the party is lost.");
                }
            }
        }

        private static void Announce(string text)
        {
            try
            {
                if (GUIGameHandler.TryGet(out GUIGameHandler gui) && gui != null) gui.GetOverlayTextHandler()?.CreateOverlayText(text);
            }
            catch (Exception) { }

            MelonLogger.Msg("[Downed] " + text);
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

        /// <summary>Stands the local player up now if they are down - the arena between rounds.</summary>
        internal static void StandUpNow()
        {
            if (_downed != null) StandUp();
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

                    // RemoveKnockout clears the knockout flag but skips everything that stands the body up while
                    // KeepKnockoutState is set - which a story knock-out step leaves behind. After the Great Tusk
                    // capture the client was "back up" by every flag and lay on the floor for good. This knockout
                    // is this mod's, so the hold is not the story's to keep; RemotePlayer does the same for puppets.
                    actor.CustomStats.KeepKnockoutState = false;
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

                // The same switch the knockout used, left off a moment longer.
                if (actor != null)
                {
                    actor.GetHitReceiveHandler()?.DisableCollider();
                    _graced = actor;
                    _graceLeft = StandUpGraceSeconds;
                }

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

        private static void CountGrace(float unscaledDeltaTime)
        {
            if (_graced == null) return;

            _graceLeft -= unscaledDeltaTime;
            if (_graceLeft <= 0f) EndGrace();
        }

        /// <summary>Makes the player hittable again - unless they are already back on the floor.</summary>
        private static void EndGrace()
        {
            Actor? actor = _graced;
            _graced = null;
            _graceLeft = 0f;

            try
            {
                if (actor == null || _downed != null) return;

                actor.GetHitReceiveHandler()?.EnableCollider();

                // Up by every flag and still on the floor: an action cut short by the knockout can leave the stance
                // queue locked (AA_Bhvr_RestrictInteractionQueue), and RemoveKnockout's request for Normal is then
                // dropped silently - the client lay there after the Great Tusk fight until asked again by hand.
                Actor.InteractionModeModule stance = actor.GetInteractionModule();
                if (stance != null && stance.CurrentMode == Actor.EInteractionMode.KnockOut)
                {
                    stance.RestrictQueue(false);
                    stance.RequestMode(Actor.EInteractionMode.Normal);
                    StandRetries++;
                    MelonLogger.Msg("[Downed] this player was still lying after getting up; stood them up again.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Downed] ending the stand-up grace failed: " + e);
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
