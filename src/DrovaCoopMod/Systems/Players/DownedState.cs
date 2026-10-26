using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Morphing;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Players.Patches;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.UI;
using Il2CppDrova;
using Il2CppDrova.ABI;
using Il2CppDrova.Brawl;
using Il2CppDrova.HitSystem;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Puts a player who would have died on the floor instead, using the game's ReviveBlocker and brawl knockout, and gets them back up.
    /// Health.CheckEvents latches _isDead for good, so recovery has to be prevention before the blow lands.
    /// </summary>
    internal sealed class DownedState : ICoopSystem
    {
        /// <summary>Seconds down before getting up alone. Matches the game's own brawl knockout.</summary>
        private const float DownedSeconds = 5f;

        /// <summary>World units another player must stand within to help. Small, since no facing or prompt is checked.</summary>
        private const float HelpUpRange = 40f;

        /// <summary>Hardcore: seconds before a downed player dies unless a teammate gets them up.</summary>
        private const float BleedOutSeconds = 30f;

        /// <summary>Seconds another player must stand there to pull the downed one up.</summary>
        private const float HelpUpSeconds = 1.5f;

        /// <summary>Seconds a freshly stood-up player cannot be hit, so the same attacker does not floor them again at once.</summary>
        private const float StandUpGraceSeconds = 2f;

        // Teammates seen bleeding out, so each fall is called out once.
        private static readonly HashSet<PlayerId> Bleeding = new();

        /// <summary>Set while a teammate's own blow is being applied here (RemoteDamage). Read when it puts the player down.</summary>
        internal static bool TeammateBlow;

        private static float _lastWarned;

        // A teammate's fall is a quarrel, it never bleeds out.
        private static bool _downedByTeammate;

        // Decided when the player goes down, so a fight ending meanwhile cannot turn the knockout into a bleed-out.
        private static bool _downedInArenaFight;

        /// <summary>
        /// How often the guard was asked. Odd is the early pass. Never reset, since a reset in Health.CheckEvents lands
        /// between the passes and flips them, so pre-armor damage would down the player.
        /// </summary>
        private static int _asked;

        // Kept, because the game removes a blocker by reference.
        private static ReviveBlocker? _blocker;
        private static BrawlActor? _downed;
        private static Health? _guarded;
        private static float _downedFor;
        private static float _helpedFor;
        private static Actor? _graced;
        private static float _graceLeft;
        private static float _sinceStandCheck = 1f;
        private static float _sinceInstall = 5f;

        /// <summary>Hardcore deaths by bleeding out on this machine. For tests.</summary>
        internal static int BledOut { get; private set; }

        /// <summary>Whether the local player has just stood up and cannot be hit yet.</summary>
        internal static bool IsGraced => _graced != null;

        /// <summary>How many times a player still lying after getting up was stood up again. For tests.</summary>
        internal static int StandRetries { get; private set; }

        /// <summary>On the floor from a would-be lethal blow that was not a teammate's. Counts toward a lost run and bleed-out.</summary>
        internal static bool IsLethallyDown => _downed != null && !_downedByTeammate;

        /// <summary>Whether the local player is currently on the floor.</summary>
        internal static bool IsDown => _downed != null;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(ReviveBlocker), nameof(ReviveBlocker.CalculateResultDamage), typeof(DownedStatePatches), nameof(DownedStatePatches.FirstPassPrefix));
        }

        /// <summary>
        /// Lets the guard judge a blow only after armor and shield. The game asks a revive blocker early and last per damage value,
        /// and the last pass is inlined, so the early questions are told apart by counting.
        /// </summary>
        internal static bool FirstPassPrefix(ReviveBlocker __instance, HealthChange? healthChange, float dmgValue, ref float __result)
        {
            if (_blocker == null || healthChange == null || __instance.Pointer != _blocker.Pointer) return true;

            // The invincibility cheat takes no damage, but the game says so only after the blockers ran.
            if (_guarded != null && _guarded.CheatInvincible)
            {
                __result = dmgValue;
                return false;
            }

            _asked++;

            // The host reports this creature's blow, so landing it here too would charge it twice.
            if (RemoteEnemyBlows.HostAnswers(healthChange.Source))
            {
                __result = 0f;
                return false;
            }

            DmgSourceType? source = healthChange._dmgSourceType;
            bool blockable = source == null || source.IsBlockable;
            if (!blockable || _asked % 2 == 0) return true;

            __result = dmgValue;
            return false;
        }

        /// <summary>
        /// Installs the guard while a session is live, removes it when one ends, and counts a downed player back up.
        /// </summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Uninstall();
                return;
            }

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            // A Hardcore death is final. The guard is not put back on a body that bled out.
            if (player.IsDead()) return;

            // A morph creature must really die: its death ends the morph and the blow passes on to the guarded caster.
            if (MorphManager.IsMorphed) return;

            Install(player);
            CountUp(unscaledDeltaTime);
            WatchTeammates();
            CountGrace(unscaledDeltaTime);

            // A save written while down loads lying with nothing to stand the player up. Checked only in a new body's
            // first seconds, since later a hit's knockdown effect is the game's own and must not be cut short.
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

        /// <summary>Takes the guard off and stands the player up, so leaving a session cannot strand them.</summary>
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

        /// <summary>Adds the blocker to the current local player, re-checked since a save load replaces the actor.</summary>
        private static void Install(Actor player)
        {
            Health? health =player.GetHealth();
            if (health == null) return;

            if (_guarded != null && _guarded.Pointer == health.Pointer) return;

            _blocker = new ReviveBlocker();
            _guarded = health;
            _sinceInstall = 0f;

            // The only signal that the blocker fired, raised from inside the damage calculation like the game's own.
            _blocker.ReviveEvent.AddEventListener(new Action<Entity>(GoDown));

            health.AddDamageBlocker(_blocker.Cast<IDamageBlocker>());

            MelonLogger.Msg("[Downed] guard installed; a killing blow will put this player down instead.");
        }

        /// <summary>Puts the player on the floor.</summary>
        private static void GoDown(Entity? saved)
        {
            try
            {
                if (_downed != null) return;

                Actor? actor = saved?.TryCast<Actor>();
                if (actor == null) return;

                BrawlActor brawler = new(actor);

                // Dialogue first, stopped on the NPC's controller the way the game ends one.
                // Otherwise the dialogue system keeps the camera, and camera-loaded volumes (exits, teleporters) stay off.
                if (Il2CppDrova.DialogueNew.DialogueSystemGameHandler.TryGet(out Il2CppDrova.DialogueNew.DialogueSystemGameHandler dialogues)
                    && dialogues.ActivePlayerDialogue != null && dialogues.ActivePlayerDialogue.isRunning)
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

                MelonLogger.Msg(Checkpoint.Rules == GameMode.Hardcore && !Arena.HoldsLocal() && !_downedInArenaFight && !_downedByTeammate
                    ? $"[Downed] this player is down and bleeding out: {BleedOutSeconds:0}s for somebody to stand over them."
                    : $"[Downed] this player is down; getting up in {DownedSeconds:0.0}s, or in {HelpUpSeconds:0.0}s if somebody stands over them.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Downed] going down failed: " + e);
            }
        }

        /// <summary>Counts a downed player back up, and lets the game or another player cut that short.</summary>
        private static void CountUp(float unscaledDeltaTime)
        {
            if (_downed == null) return;

            // In an arena round a downed fighter stays down until the round is decided.
            if (Arena.KeepsDown()) return;

            _downedFor += unscaledDeltaTime;

            // ForceEndKnockout is what a help-up interaction sets.
            bool forced = _downed.GetActor() != null && _downed.GetActor().CustomStats.ForceEndKnockout;
            bool helped = IsBeingHelped(unscaledDeltaTime);

            if (forced || helped)
            {
                StandUp(helped);
                return;
            }

            // Hardcore outside any arena fight: no getting up alone.
            if (Checkpoint.Rules == GameMode.Hardcore && !Arena.HoldsLocal() && !_downedInArenaFight && !_downedByTeammate)
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
                // Called out at the fall and every ten seconds, then the last five counted.
                float mark = left > 5f ? Mathf.Ceil(left / 10f) * 10f : Mathf.Ceil(left);
                if (mark != _lastWarned)
                {
                    _lastWarned = mark;
                    Announce($"You are bleeding out - {Mathf.CeilToInt(left)} s. A teammate must get you up.");
                }

                return;
            }

            // Too late: a real death. The game's own death follows and the host ends the run (GameOver).
            Actor? actor = _downed?.GetActor();
            if (actor == null) return;

            MelonLogger.Msg("[Downed] Hardcore: nobody came in time; this player bled out.");
            BledOut++;
            Health? health = actor.GetHealth();
            if (_blocker != null && health != null) health.RemoveDamageBlocker(_blocker.Cast<IDamageBlocker>());
            _blocker = null;
            _guarded = null;
            _downed = null;

            // Written, not dealt, since a knocked-out body takes no blows. Zero latches the game's own death.
            if (health != null) health.SetHealth(0, null);
        }

        /// <summary>Hardcore: calls out a teammate who went down, so the others know the clock is running.</summary>
        private static void WatchTeammates()
        {
            if (Checkpoint.Rules != GameMode.Hardcore)
            {
                Bleeding.Clear();
                return;
            }

            foreach ((PlayerId id, RemotePlayer body) in RemotePlayers.Bodies)
            {
                bool bleeding = body.IsOut && !body.IsDead && !Arena.Holds(id);
                if (!bleeding)
                {
                    Bleeding.Remove(id);
                    continue;
                }

                if (Bleeding.Add(id))
                {
                    string name = CoopSession.Players.GetValueOrDefault(id, "A teammate");
                    Announce($"{name} is bleeding out! Stand over them within {BleedOutSeconds:0} s or the party is lost.");
                }
            }
        }

        private static void Announce(string text)
        {
            Overlay.Show(text);

            MelonLogger.Msg("[Downed] " + text);
        }

        /// <summary>
        /// Whether another upright player has stood over this one long enough. Decided by the downed player, so no message is needed.
        /// </summary>
        private static bool IsBeingHelped(float unscaledDeltaTime)
        {
            Actor? actor = _downed?.GetActor();
            if (actor == null) return false;

            if (!AnyoneStandingOver(actor))
            {
                // Reset so helping has to be continuous.
                _helpedFor = 0f;
                return false;
            }

            _helpedFor += unscaledDeltaTime;

            return _helpedFor >= HelpUpSeconds;
        }

        /// <summary>Whether any remote player's body is close enough, and upright enough, to be helping.</summary>
        private static bool AnyoneStandingOver(Actor actor)
        {
            Vector3 down = actor.transform.position;

            foreach (RemotePlayer other in RemotePlayers.Bodies.Values)
            {
                Actor body = other.Body;
                if (body == null) continue;

                if (body.CustomStats != null && body.CustomStats.IsKnockout) continue;

                if ((body.transform.position - down).sqrMagnitude <= HelpUpRange * HelpUpRange) return true;
            }

            return false;
        }

        /// <summary>Stands the local player up now if they are down, as the arena does between rounds.</summary>
        internal static void StandUpNow()
        {
            if (_downed != null) StandUp();
        }

        /// <summary>Stands the player back up.</summary>
        /// <param name="helped">Whether another player pulled them up. Only changes the log line.</param>
        private static void StandUp(bool helped = false)
        {
            try
            {
                if (_downed == null) return;

                Actor? actor = _downed.GetActor();
                if (actor != null)
                {
                    actor.CustomStats.ForceEndKnockout = false;

                    // A story knockout step leaves KeepKnockoutState set, which makes RemoveKnockout skip standing up.
                    // This knockout is the mod's, so the hold is cleared.
                    actor.CustomStats.KeepKnockoutState = false;
                }

                _downed.RemoveKnockout();

                // The order HealthBhvr_ReviveBhvr.Revive uses. RequestHeal does nothing for a player (only an AI graph reads it),
                // the health write below is what heals.
                _downed.RequestHeal();
                HealForRecovery(actor);

                // The same switch the knockout used, left off a moment longer.
                if (actor != null)
                {
                    HitReceiveHandler? hits = actor.GetHitReceiveHandler();
                    if (hits != null) hits.DisableCollider();
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

        /// <summary>Makes the player hittable again, unless they are already back on the floor.</summary>
        private static void EndGrace()
        {
            Actor? actor = _graced;
            _graced = null;
            _graceLeft = 0f;

            try
            {
                if (actor == null || _downed != null) return;

                HitReceiveHandler? hits = actor.GetHitReceiveHandler();
                if (hits != null) hits.EnableCollider();

                // A cut-short action can leave the stance queue locked (AA_Bhvr_RestrictInteractionQueue),
                // silently dropping RemoveKnockout's request for Normal.
                Actor.InteractionModeModule? stance =actor.GetInteractionModule();
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
        /// Raises a stood-up player to the host's recovery share (<see cref="CoopSession.RecoveryHealthPercent"/>), never lowers.
        /// </summary>
        private static void HealForRecovery(Actor? actor)
        {
            if (actor == null) return;

            Health? health = actor.GetHealth();
            if (health == null) return;

            int wanted = Mathf.RoundToInt(health.MaxHealth * (CoopSession.RecoveryHealthPercent / 100f));
            if (wanted < 1) wanted = 1;
            if (wanted <= health.CurHealth) return;

            health.SetHealth(wanted, null);
        }
    }
}
