using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.StaminaSystem;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Sends what the local player is trying to do, at a rate that does not depend on how fast this
    /// machine renders.
    ///
    /// The transport is pumped from the melon's update, so sending straight from there would tie the send
    /// rate to the framerate: a 144 fps host would produce nearly five times the packets of a 30 fps
    /// client, and the two would disagree about how much history a second of buffer holds. Everything
    /// downstream - correction thresholds, jitter budget, bandwidth - assumes a known rate, so it is worth
    /// fixing here rather than compensating for everywhere else.
    /// </summary>
    internal static class LocalPlayerBroadcaster
    {
        /// <summary>
        /// Sends per second, for everything that is not the start of an action.
        ///
        /// Chosen against what the receiver does with them: it eases toward the newest position, so the
        /// gap between packets is the longest it can be working from stale data. At 30 Hz that is 33 ms,
        /// short enough that the easing has nothing visible to catch up on. Halving it to 15 Hz doubles
        /// that gap into the range where direction changes arrive visibly late; doubling it to 60 Hz
        /// doubles the bandwidth to say the same thing, since the state changes no faster than the player
        /// does.
        ///
        /// Raising it is also the wrong answer for combat, which is what this rate would otherwise be
        /// blamed for. A swing's animation is not streamed - the far machine plays the whole thing itself
        /// once the start is accepted - so more packets buy nothing but a slightly earlier start, on every
        /// packet, most of which describe somebody standing still. A change of action is therefore sent
        /// the moment it happens and pays that cost only when there is something to pay it for.
        ///
        /// What matters more than the exact number is that every machine uses the same one. The receiver's
        /// easing rate is tuned against this interval, so a peer sending at half the rate would be eased
        /// as though its packets were merely late.
        /// </summary>
        private const float SendsPerSecond = 30f;

        private const float SendInterval = 1f / SendsPerSecond;

        private static float _sinceLastSend;

        // What the player was doing at the last send, and how many distinct things they have done. The
        // receiver needs the count rather than only the action: two swings in a row read the same, and
        // these go out unreliable, so it also has to be able to tell a new swing from an old packet
        // arriving late.
        private static ActionBinds.Snapshot _lastAction = ActionBinds.Idle;
        private static byte _actionSequence;

        /// <summary>
        /// Sends the local player's state when the next tick is due, and the moment a swing begins.
        ///
        /// Position is paced; combat is not. Where a player is can wait for the next tick, because the
        /// receiver eases towards it and a third of a frame of staleness is invisible. When a swing begins
        /// cannot: an attack is an instant, the far body has a combo window to hit, and waiting up to
        /// 33 ms to mention it spends a third of that window on nothing. So a change of action sends
        /// immediately and everything else keeps to the clock.
        ///
        /// This costs a handful of extra packets during a fight and none at all the rest of the time,
        /// which is the opposite trade to raising the rate for everybody: doubling the tick would pay for
        /// the same latency on every packet, most of which describe a player who is standing still.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame, unaffected by
        /// <c>Time.timeScale</c>. The receiving side runs on unscaled time too, so a paused menu here must
        /// not stretch the gaps it sees.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Reset();
                return;
            }

            _sinceLastSend += unscaledDeltaTime;

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            // Read every frame rather than every tick, because a change that is noticed late cannot then
            // be sent early. It costs nothing while the player is not acting - the module answers that in
            // one call - and a walk over the binds only while a swing is running.
            ActionBinds.Snapshot action = ActionBinds.Current(player.GetActionModule());

            // Two questions, and conflating them replicated every attack twice. A new use is a new swing
            // and is what the sequence counts; letting go is the same swing continuing and must not touch
            // the count, or the receiver reads the release packet as a second attack. Both are worth
            // sending at once, because a release that waits for the next tick turns a tap into a charge.
            bool newUse = !action.IsSameUse(_lastAction);
            bool actionChanged = newUse || action.Released != _lastAction.Released;

            if (!actionChanged && _sinceLastSend < SendInterval) return;

            if (actionChanged)
            {
#if DEBUG
                // Every transition this side believes it saw. A combo that does not replicate is either
                // one the sender never noticed or one the receiver refused, and those have opposite fixes
                // - so the two ends are logged in the same terms and the pair says which it was. The
                // release is named because a use that is only released carries the sequence it already had.
                MelonLogger.Msg($"[Act] local {_lastAction.Bind}/u{_lastAction.Usage}/c{_lastAction.Combo} -> " +
                    $"{action.Bind}/u{action.Usage}/c{action.Combo}, released {action.Released}, " +
                    $"seq {unchecked((byte)(_actionSequence + (newUse ? 1 : 0)))}");
#endif

                _lastAction = action;

                if (newUse)
                {
                    _actionSequence = unchecked((byte)(_actionSequence + 1));
                }

                // The clock restarts rather than carrying on, so the fixed tick does not fire again a
                // millisecond after this one. The packet just sent carries the position too, so nothing
                // downstream has been skipped - only brought forward.
                _sinceLastSend = 0f;
            }
            else
            {
                // Accumulated rather than reset to zero, so a frame that overshoots does not push every
                // later send late; the rate stays honest over time instead of drifting with the framerate.
                // Clamped so a long hitch does not then fire a burst of catch-up sends.
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

            // The action is not reset with it. The sequence has to keep climbing across a rejoin, because
            // the other side may still hold the last one it saw and would take a restarted count for a
            // packet from the past.
            _lastAction = ActionBinds.Idle;
        }

        /// <summary>
        /// Reads the same intent the local input layer produces and puts it on the wire.
        /// </summary>
        private static void Send(Actor player, in ActionBinds.Snapshot action)
        {
            Actor.MovementModule movement = player.GetMovementModule();
            Actor.LookModule look = player.GetLookModule();

            Vector2 direction = movement.DirectionModule.DirectionVector();

            // The target rather than the current look direction: the current one is mid-turn, so sending
            // it would make the remote body chase a turn that is already being interpolated at the far end.
            Vector2 lookDirection = look.TargetLookDir;
            Vector3 position = player.transform.position;

            // Asked for rather than assumed. Health is a component, and this reads a body the mod does not
            // build; a player without one sends zeroes, which the far end already treats as "not known".
            ushort health = 0;
            ushort maxHealth = 0;

            if (player.HasHealth())
            {
                Health playerHealth = player.GetHealth();

                health = ToWire(playerHealth.CurHealth);
                maxHealth = ToWire(playerHealth.MaxHealth);
            }

            // Rounded to whole points on the way out. The far end uses this to decide whether a guard
            // breaks, and that comparison is against a cost of several points - a fraction either way
            // cannot change the answer, and two bytes on a thirty-hertz packet is worth the rounding.
            ushort stamina = 0;
            ushort maxStamina = 0;

            StaminaController staminaController = player.GetStaminaController();
            if (staminaController != null)
            {
                stamina = ToWire((int)MathF.Round(staminaController.CurStamina));
                maxStamina = ToWire((int)MathF.Round(staminaController.MaxStamina));
            }

            PlayerStateMessage message = new()
            {
                PlayerId = CoopSession.LocalPlayerId,
                X = position.x,
                Y = position.y,
                DirectionX = direction.x,
                DirectionY = direction.y,
                LookX = lookDirection.x,
                LookY = lookDirection.y,
                MoveMode = (byte)movement.IntendedMode,

                // The target rather than the current mode, for the same reason as the look direction:
                // drawing a weapon is a transition with its own animation, and the far end should be told
                // where the player is going so it can play that transition itself rather than being told
                // where they are part-way through it.
                InteractionMode = (byte)player.GetInteractionModule().TargetMode,
                Action = (byte)action.Bind,
                ActionReleased = action.Released ? (byte)1 : (byte)0,
                ActionSequence = _actionSequence,
                Health = health,
                MaxHealth = maxHealth,
                Stamina = stamina,
                MaxStamina = maxStamina,

                // The mod's own downed state, and the game's knockout with it. The second is not
                // redundant: a scripted brawl knocks this player out through machinery that runs only on
                // this machine - the brawl even disarms the revive blocker the coop downed state hooks -
                // so without reading the actor, a player beaten in a duel lies on the floor here while
                // their body far away stands upright and is attacked forever.
                Downed = DownedState.IsDown
                    || (player.CustomStats != null && player.CustomStats.IsKnockout)
                    ? (byte)1 : (byte)0,
            };

            CoopSession.BroadcastPlayerState(message);
        }

        /// <summary>
        /// Fits a health figure into the two bytes the message carries.
        ///
        /// The game's own type is <c>int</c> and nothing in a normal game comes near the ceiling, but a
        /// talent or an item stacking max health is exactly the sort of thing that is not bounded on
        /// purpose. Clamping keeps an overflow as a health bar that stops climbing, rather than as one
        /// that wraps to nearly empty and reads as the other player being about to die.
        /// </summary>
        private static ushort ToWire(int health)
        {
            if (health < 0) return 0;

            return health > ushort.MaxValue ? ushort.MaxValue : (ushort)health;
        }
    }
}
