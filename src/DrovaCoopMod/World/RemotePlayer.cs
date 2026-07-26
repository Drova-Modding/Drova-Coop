using DrovaCoopMod.Messages;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// One remote player: the body shown for them, split between what it looks like and where it is.
    ///
    /// **Intent decides appearance.** The direction, move mode and look direction the far player's input
    /// layer produced are handed to the same modules the local player's input drives, so the body faces
    /// the right way and plays the right animation without any of that being reimplemented.
    ///
    /// **The wire decides position.** Intent alone cannot move a clone: it has no input controller, and
    /// on the real player it is that controller which drives the movement module every frame. Set a
    /// direction on a clone and it mimes running on the spot forever - measured at a flat 11.0 units of
    /// error for fifty seconds, with a valid direction and a populated speed stat.
    ///
    /// Making the body walk itself needs the clone's modules ticked. That is worth doing when combat
    /// needs it to collide and be hit; until then, carrying it along the path its owner actually took
    /// looks the same and is honest about what it is.
    /// </summary>
    internal sealed class RemotePlayer
    {
        /// <summary>
        /// How quickly the body closes the gap to where its owner says it is, as a rate rather than a
        /// speed: it covers roughly this fraction of the remaining distance per second. High enough that
        /// it is never visibly behind, low enough that a late packet does not read as a twitch.
        /// </summary>
        private const float FollowRate = 14f;

        /// <summary>
        /// Past this, the body is not corrected but relocated, and instantly: a player who teleported,
        /// changed scene, or was out of contact should not appear to sprint across the map.
        /// </summary>
        private const float TeleportDistance = 50f;

        private PlayerStateMessage _state;
        private bool _hasState;

#if DEBUG
        private float _sinceDriftReport;
#endif

        /// <summary>
        /// Creates a remote player around an already-built body.
        /// </summary>
        internal RemotePlayer(PlayerId id, Actor body)
        {
            Id = id;
            Body = body;
        }

        /// <summary>Who this is.</summary>
        internal PlayerId Id { get; }

        /// <summary>The body being shown for them.</summary>
        internal Actor Body { get; }

        /// <summary>
        /// Takes the newest state. Latest wins outright: the easing in <see cref="Follow"/> is what
        /// smooths arrival, so buffering older states here would only add lag on top of it.
        /// </summary>
        internal void Record(PlayerStateMessage state)
        {
            _state = state;
            _hasState = true;
        }

        /// <summary>
        /// Points the body the way its owner is facing and carries it to where its owner is.
        /// </summary>
        internal void Apply(float unscaledDeltaTime)
        {
            if (!_hasState || Body == null) return;

            try
            {
                Actor.MovementModule movement = Body.GetMovementModule();

#if DEBUG
                ReportDrift(movement, unscaledDeltaTime);
#endif

                if (TryRelocate(movement)) return;

                // Intent decides what the body looks like: which way it faces, whether it is walking,
                // running or standing, and therefore which animation plays.
                movement.SetIntendedMode(ToMoveMode(_state.MoveMode));
                movement.SetDirection(new Vector2(_state.DirectionX, _state.DirectionY));

                // A zero look direction means the sender never had one; sending it on would snap the body
                // to face east rather than leaving it as it was.
                Vector2 look = new(_state.LookX, _state.LookY);
                if (look.sqrMagnitude > 0.0001f)
                {
                    Body.GetLookModule().SetLookDirection(look);
                }

                Follow(movement, unscaledDeltaTime);
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Puppet] applying intent to {Id} failed: " + e);
            }
        }

        /// <summary>
        /// Moves the body to where its owner says it is, easing rather than jumping.
        ///
        /// The body has to be carried here rather than walking itself. A clone has no input controller,
        /// and on the real player it is that controller which drives the movement module every frame -
        /// so on a clone, setting a direction decides the animation and the facing and then nothing
        /// integrates it. Measured: with a valid direction and a populated speed stat, a body left to
        /// walk itself sat exactly 11.0 units from where it belonged for fifty seconds. <c>MoveDelta</c>
        /// does not help, because it too depends on an update that is not running.
        ///
        /// So position comes from the wire and appearance comes from intent, and the two together read
        /// correctly: the character animates its own run while being carried along the path its owner
        /// actually took. Driving it properly needs the clone's modules ticked, which is worth doing when
        /// combat needs the body to collide and be hit - not before.
        ///
        /// Exponential easing rather than a fixed step so the rate is frame-rate independent: the body
        /// closes the same fraction of the remaining gap per second however often this runs.
        /// </summary>
        private void Follow(Actor.MovementModule movement, float unscaledDeltaTime)
        {
            Vector3 here = Body.transform.position;
            Vector3 authoritative = Authoritative();

            float blend = 1f - Mathf.Exp(-FollowRate * unscaledDeltaTime);
            Vector3 next = Vector3.Lerp(here, authoritative, blend);

            movement.SetPosition(new Vector2(next.x, next.y));
        }

#if DEBUG
        /// <summary>
        /// Reports how far the body is from where its owner says it is, and how fast it is being told to
        /// move. Steady drift growing at the sender's own speed means the body is not translating at all,
        /// which looks identical to working replication until something measures it.
        /// </summary>
        private void ReportDrift(Actor.MovementModule movement, float unscaledDeltaTime)
        {
            _sinceDriftReport += unscaledDeltaTime;
            if (_sinceDriftReport < 3f) return;

            _sinceDriftReport = 0f;

            float drift = Vector3.Distance(Body.transform.position, Authoritative());
            Vector2 direction = new(_state.DirectionX, _state.DirectionY);

            MelonLogger.Msg($"[Puppet] {Id} drift={drift:0.0} desiredSpeed={movement.GetDesiredSpeed():0.0} " +
                $"direction={direction.magnitude:0.00} mode={_state.MoveMode}");
        }
#endif

        /// <summary>
        /// Moves the body outright when the gap is too large to be drift - a teleport, a scene change, a
        /// spell of no contact. Nobody can walk that off, and trying to looks worse than admitting it.
        /// </summary>
        private bool TryRelocate(Actor.MovementModule movement)
        {
            float drift = Vector3.Distance(Body.transform.position, Authoritative());
            if (drift <= TeleportDistance) return false;

            movement.SetPosition(new Vector2(_state.X, _state.Y));
            MelonLogger.Msg($"[Puppet] {Id} relocated {drift:0} units; it had gone out of contact or teleported.");
            return true;
        }

        private Vector3 Authoritative()
        {
            return new Vector3(_state.X, _state.Y, Body.transform.position.z);
        }

        /// <summary>
        /// Turns a wire value into a move mode, clamping anything this build does not know. The enum's
        /// numbering belongs to the game, so a peer on a different version could name a mode that does not
        /// exist here, and casting that straight through is undefined behavior waiting for a game update.
        /// </summary>
        private static Actor.EMoveMode ToMoveMode(byte value)
        {
            return value switch
            {
                (byte)Actor.EMoveMode.Stand => Actor.EMoveMode.Stand,
                (byte)Actor.EMoveMode.Walk => Actor.EMoveMode.Walk,
                (byte)Actor.EMoveMode.Run => Actor.EMoveMode.Run,
                _ => Actor.EMoveMode.Stand,
            };
        }
    }
}
