using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.StaminaSystem;
using Il2CppDrova.Talent;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Sends what the local player is trying to do at a fixed rate, independent of the framerate,
    /// since the receiver's easing is tuned against it.
    /// </summary>
    internal static class LocalPlayerBroadcaster
    {
        /// <summary>
        /// Sends per second for everything but action changes, which go out at once. At 15 Hz direction
        /// changes arrive visibly late, and 60 Hz doubles bandwidth for nothing. Every machine must match.
        /// </summary>
        private const float SendsPerSecond = 30f;

        private const float SendInterval = 1f / SendsPerSecond;

        private static float _sinceLastSend;

        /// <summary>
        /// How often an unchanged state goes out anyway, well inside every timeout downstream.
        /// </summary>
        private const float KeepaliveSeconds = 0.4f;

        private static PlayerStateMessage _lastSent;
        private static float _lastSentAt = float.NegativeInfinity;

        // The use count lets the receiver tell two identical swings apart, and a new swing from a late packet.
        private static ActionBinds.Snapshot _lastAction = ActionBinds.Idle;
        private static byte _actionSequence;

        /// <summary>Whether the current use continues the chain before it. Sent with every packet of that use.</summary>
        private static bool _actionIsCombo;

        /// <summary>The swing whose combo click was announced, so it is announced once.</summary>
        private static IntPtr _comboAnnouncedFor;

        /// <summary>Whether a combo step was announced at its click and has not begun yet.</summary>
        private static bool _comboAnnounced;

        /// <summary>Whether this swing's combo was clicked for and is armed to begin when the swing ends.</summary>
        private static bool IsComboTriggered(Il2CppDrova.ActorActions.AA_ABase swing)
        {
            Il2CppDrova.ActorActions.AA_Atk_BhvrChanges? changes = swing.GetComponent<Il2CppDrova.ActorActions.AA_Atk_BhvrChanges>();
            return changes != null && changes._combo != null && changes._combo._comboTriggered;
        }

        /// <summary>The prefab hash of the unbound action now running, or 0. See <see cref="PlayerStateMessage.ActionPrefab"/>.</summary>
        private static uint _actionPrefab;

        private static ushort _lastMaxHealth;
        private static ushort _lastMaxStamina;

        private static byte _lastVitalFlags;
        private static int _lastRosterCount;

        /// <summary>
        /// Sends the local player's state when the next tick is due, and at once when the action changes,
        /// so the far body does not miss its combo window.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame, unscaled like the receiver's clock.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Reset();
                return;
            }

            _sinceLastSend += unscaledDeltaTime;

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            // A body mid-reload can be missing its modules for a frame.
            if (player.GetActionModule() == null || player.GetMovementModule()?.DirectionModule == null
                || player.GetLookModule() == null || player.GetInteractionModule() == null) return;

            // Unbound actions (mining, fishing, loading a sling) go by prefab, but only from idle, since a
            // bow shot's tail matches no bind briefly. Actions that cross as item uses are left out.
            ActionBinds.Snapshot action = ActionBinds.Observe(player.GetActionModule(), out Il2CppDrova.ActorActions.AA_ABase? rootPrefab);
            uint actionPrefab = 0;
            if (action.Bind == Actor.ActionModule.EActionBind.None)
            {
                Il2CppDrova.ActorActions.AA_ABase? running = player.GetActionModule().CurrentAction;
                bool fromIdle = _lastAction.Bind == Actor.ActionModule.EActionBind.None;
                bool crossesAsItemUse = running?.SourceItem != null && ItemUseWatch.IsWorthReplicating(running.SourceItem);
                if (rootPrefab != null && running != null && !crossesAsItemUse && fromIdle) actionPrefab = ActionPrefabIndex.Hash(rootPrefab.name);
                else action = ActionBinds.Idle;
            }

            // A release continues the same swing and must not bump the sequence. Both go out at once,
            // since a late release turns a tap into a charge.
            bool newUse = !action.IsSameUse(_lastAction);

            // A combo step is announced at the click, since AA_Atk_Combo starts it only at the swing's end,
            // too late for a lagging copy. The step starting later does not count again.
            Il2CppDrova.ActorActions.AA_ABase? swing = player.GetActionModule().CurrentAction;
            bool comboClick = !newUse && action.Bind != Actor.ActionModule.EActionBind.None && swing != null
                && swing.Pointer != _comboAnnouncedFor && IsComboTriggered(swing);
            bool stepStarting = newUse && action.Combo > 0 && _comboAnnounced;
            bool countsAsUse = (newUse && !stepStarting) || comboClick;
            bool actionChanged = newUse || comboClick || action.Released != _lastAction.Released;

            if (!actionChanged && _sinceLastSend < SendInterval) return;

            if (actionChanged)
            {
#if DEBUG
                // Logged in the receiver's terms, so the pair shows which side lost a combo.
                MelonLogger.Msg($"[Act] local {_lastAction.Bind}/u{_lastAction.Usage}/c{_lastAction.Combo} -> " +
                    $"{action.Bind}/u{action.Usage}/c{action.Combo}, released {action.Released}, " +
                    $"seq {unchecked((byte)(_actionSequence + (countsAsUse ? 1 : 0)))}{(comboClick ? ", combo click" : "")}");
#endif

                if (countsAsUse)
                {
                    _actionSequence = unchecked((byte)(_actionSequence + 1));

                    // A combo step has a combo count above zero. The child's use counter is its own.
                    _actionIsCombo = comboClick || (action.Bind != Actor.ActionModule.EActionBind.None && action.Combo > 0);
                }

                if (comboClick)
                {
                    _comboAnnouncedFor = swing!.Pointer;
                    _comboAnnounced = true;
                }
                else if (newUse)
                {
                    _comboAnnounced = false;
                }

                _lastAction = action;
                _actionPrefab = actionPrefab;

                // Restart the clock, since this packet carries the position too.
                _sinceLastSend = 0f;
            }
            else
            {
                // Carried over so the rate does not drift with the framerate, clamped so a hitch cannot burst.
                _sinceLastSend -= SendInterval;
                if (_sinceLastSend > SendInterval) _sinceLastSend = SendInterval;
            }

            Send(player, action);
        }

        /// <summary>
        /// Forgets the send clock, so rejoining does not start mid-interval.
        /// </summary>
        internal static void Reset()
        {
            _sinceLastSend = 0f;
            _lastSentAt = float.NegativeInfinity;

            // The sequence keeps climbing across a rejoin, or the far side takes a restart for a stale packet.
            _lastAction = ActionBinds.Idle;

            _lastMaxHealth = 0;
            _lastMaxStamina = 0;
            _lastVitalFlags = 0;
            _lastRosterCount = 0;
        }

        private static void Send(Actor player, in ActionBinds.Snapshot action)
        {
            Actor.MovementModule movement = player.GetMovementModule();
            Actor.LookModule look = player.GetLookModule();

            Vector2 direction = movement.DirectionModule.DirectionVector();

            // The target, not the current mid-turn direction the far end interpolates anyway.
            Vector2 lookDirection = look.TargetLookDir;
            Vector3 position = player.transform.position;

            // Zero health means "not known" on the far end.
            ushort health = 0;
            ushort maxHealth = 0;

            if (player.HasHealth())
            {
                Health playerHealth = player.GetHealth();

                health = (ushort)Math.Clamp(playerHealth.CurHealth, 0, ushort.MaxValue);
                maxHealth = (ushort)Math.Clamp(playerHealth.MaxHealth, 0, ushort.MaxValue);
            }

            // Whole points are enough for the far end's guard-break check.
            ushort stamina = 0;
            ushort maxStamina = 0;

            StaminaController staminaController = player.GetStaminaController();
            if (staminaController != null)
            {
                stamina = (ushort)Math.Clamp((int)MathF.Round(staminaController.CurStamina), 0, ushort.MaxValue);
                maxStamina = (ushort)Math.Clamp((int)MathF.Round(staminaController.MaxStamina), 0, ushort.MaxValue);
            }

            SendVitalsIfChanged(maxHealth, maxStamina);

            bool moving = PlayerStateMessage.TryPackAngle(direction.x, direction.y, out byte moveAngle);
            PlayerStateMessage.TryPackAngle(lookDirection.x, lookDirection.y, out byte lookAngle);

            PlayerStateMessage message = new()
            {
                PlayerId = CoopSession.LocalPlayerId,
                X = position.x,
                Y = position.y,
                MoveAngle = moveAngle,
                LookAngle = lookAngle,
                MoveMode = (byte)movement.IntendedMode,

                // The target mode, so the far end plays the draw transition itself.
                InteractionMode = (byte)player.GetInteractionModule().TargetMode,
                Action = (byte)action.Bind,
                ActionSequence = _actionSequence,
                Health = health,
                Stamina = stamina,
            };

            message.Moving = moving;
            message.ActionReleased = action.Released;
            message.ActionCombo = _actionIsCombo;
            message.ActionPrefab = _actionPrefab;

            // The game's knockout too, since a scripted brawl knocks out the player without the mod's downed state.
            message.Downed = DownedState.IsDown || (player.CustomStats != null && player.CustomStats.IsKnockout);
            message.Out = DownedState.IsLethallyDown || player.IsDead();
            message.InBrawl = player.CustomStats != null && player.CustomStats.IsInBrawl;

            float now = Time.unscaledTime;
            if (message.Equals(_lastSent) && now - _lastSentAt < KeepaliveSeconds) return;

            _lastSent = message;
            _lastSentAt = now;
            CoopSession.BroadcastPlayerState(message);
        }

        /// <summary>
        /// Sends the maxima reliably when they change or somebody joins.
        /// </summary>
        private static void SendVitalsIfChanged(ushort maxHealth, ushort maxStamina)
        {
            bool rosterChanged = CoopSession.Players.Count != _lastRosterCount;
            _lastRosterCount = CoopSession.Players.Count;

            byte flags = PlayerAccess.TryGetPlayer(out Actor player) ? ReadVitalFlags(player) : _lastVitalFlags;

            if (!rosterChanged && maxHealth == _lastMaxHealth && maxStamina == _lastMaxStamina
                && flags == _lastVitalFlags) return;

            _lastMaxHealth = maxHealth;
            _lastMaxStamina = maxStamina;
            _lastVitalFlags = flags;

            CoopSession.BroadcastVitals(maxHealth, maxStamina, flags);
        }

        /// <summary>
        /// The <see cref="PlayerVitalsMessage"/> flags a puppet cannot work out for itself.
        /// </summary>
        private static byte ReadVitalFlags(Actor player)
        {
            try
            {
                ITalentModule talents = player.TalentActorModule;
                if (talents == null) return 0;

                GlobalAssetsGameHandler assets = GlobalAssetsGameHandler.TryGet();
                TalentContainer perfectBlock = assets?.PerfectBlockTalent!;
                if (perfectBlock == null) return 0;

                return talents.HasTalentLearned(perfectBlock.GUID) ? PlayerVitalsMessage.FlagPerfectBlock : (byte)0;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[State] reading this player's talents failed: " + e);
                return 0;
            }
        }
    }
}
