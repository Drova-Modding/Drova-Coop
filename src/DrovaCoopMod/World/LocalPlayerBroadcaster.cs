using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
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
        /// Sends per second. Nitrox uses 30 for movement and it is a reasonable place to start; the number
        /// matters less than it being the same on every machine.
        /// </summary>
        private const float SendsPerSecond = 30f;

        private const float SendInterval = 1f / SendsPerSecond;

        private static float _sinceLastSend;

        /// <summary>
        /// Sends the local player's state when the next tick is due.
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
            if (_sinceLastSend < SendInterval) return;

            // Accumulated rather than reset to zero, so a frame that overshoots does not push every later
            // send late; the rate stays honest over time instead of drifting with the framerate. Clamped
            // so a long hitch does not then fire a burst of catch-up sends.
            _sinceLastSend -= SendInterval;
            if (_sinceLastSend > SendInterval) _sinceLastSend = SendInterval;

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            Send(player);
        }

        /// <summary>
        /// Forgets the send clock, so rejoining does not start mid-interval.
        /// </summary>
        internal static void Reset()
        {
            _sinceLastSend = 0f;
        }

        /// <summary>
        /// Reads the same intent the local input layer produces and puts it on the wire.
        /// </summary>
        private static void Send(Actor player)
        {
            Actor.MovementModule movement = player.GetMovementModule();
            Actor.LookModule look = player.GetLookModule();

            Vector2 direction = movement.DirectionModule.DirectionVector();

            // The target rather than the current look direction: the current one is mid-turn, so sending
            // it would make the remote body chase a turn that is already being interpolated at the far end.
            Vector2 lookDirection = look.TargetLookDir;
            Vector3 position = player.transform.position;

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
            };

            CoopSession.BroadcastPlayerState(message);
        }
    }
}
