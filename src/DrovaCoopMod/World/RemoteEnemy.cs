using DrovaCoopMod.Messages;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// One creature the host drives, seen from a client.
    ///
    /// A stripped <see cref="RemotePlayer"/>: the same idea, with everything that only makes sense for a
    /// character removed. Position, facing, movement and actions are all calls on the actor modules that
    /// every actor has, so they transfer unchanged - the mid-action equipment refusal is the only thing in
    /// that machinery gated on being a player, and it is exactly the part a creature does not need.
    /// </summary>
    internal sealed class RemoteEnemy
    {
        /// <summary>
        /// How long a creature is driven after its last update before it is told to stand still.
        ///
        /// **Not optional, and not a tidiness measure.** A creature whose AI has been switched off keeps
        /// moving in whatever direction it was last given, forever - the movement module takes its
        /// move-with-direction branch precisely when the AI is inactive. So a creature that leaves the
        /// host's interest set, or whose updates are lost, walks away across the map until something stops
        /// it. This is what stops it.
        /// </summary>
        private const float StaleSeconds = 0.5f;

        private readonly Actor _body;

        private bool _hasTakenOver;
        private float _sinceUpdate;
        private bool _isStopped;
        private byte _appliedActionSequence;
        private bool _hasAppliedAction;

        /// <summary>
        /// Wraps a body the host has claimed the driving of.
        /// </summary>
        /// <param name="body">The creature.</param>
        internal RemoteEnemy(Actor body)
        {
            _body = body;
        }

        /// <summary>The body being driven.</summary>
        internal Actor Body => _body;

        /// <summary>
        /// Puts this machine's copy under the wire's control.
        ///
        /// Two calls, and the second is the one nobody expects: switching the AI off is what stops the
        /// creature deciding for itself, and zeroing its direction is what stops the body it leaves behind
        /// from sliding away at a run for the rest of the session.
        /// </summary>
        internal void TakeOver()
        {
            if (_hasTakenOver || _body == null) return;

            try
            {
                _body.AIModule.DeactivateAI();
                _body.GetMovementModule().SetDirection(Vector2.zero);

                _hasTakenOver = true;
                _isStopped = true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] taking over a creature failed: " + e);
            }
        }

        /// <summary>
        /// Gives the creature back to its own AI.
        ///
        /// **Reset before reactivate.** A behavior tree resumes whatever it was doing, and what it was
        /// doing is minutes old - a path towards a target that has long since moved. Resetting first is
        /// what makes it look at the world again rather than finish an errand nobody remembers.
        /// </summary>
        internal void HandBack()
        {
            if (!_hasTakenOver || _body == null) return;

            try
            {
                _body.GetMovementModule().SetDirection(Vector2.zero);
                _body.AIModule.ResetAI(true);
                _body.AIModule.ActivateAI();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] handing a creature back to its own AI failed: " + e);
            }
            finally
            {
                _hasTakenOver = false;
            }
        }

        /// <summary>
        /// Puts one update from the host onto this body.
        /// </summary>
        /// <param name="record">What the host says about it.</param>
        /// <param name="origin">What the record's offsets are measured from.</param>
        internal void Apply(in EnemySnapshotMessage.Record record, Vector2 origin)
        {
            if (_body == null) return;

            TakeOver();

            _sinceUpdate = 0f;
            _isStopped = false;

            try
            {
                Vector2 position = origin + new Vector2(
                    record.OffsetX * EnemySnapshotMessage.PositionStep,
                    record.OffsetY * EnemySnapshotMessage.PositionStep);

                Actor.MovementModule movement = _body.GetMovementModule();
                if (movement != null)
                {
                    movement.SetPosition(position);

                    bool isMoving = (record.Flags & EnemySnapshotMessage.FlagMoving) != 0;

                    movement.SetDirection(isMoving ? Angle(record.MoveAngle) : Vector2.zero);
                    movement.SetIntendedMode(ToMoveMode((byte)((record.Flags >> 1) & 0b11)));
                }

                Actor.LookModule look = _body.GetLookModule();
                if (look != null)
                {
                    look.SetLookDirection(Angle(record.LookAngle));
                }

                ApplyAction(record);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] driving a creature failed: " + e);
            }
        }

        /// <summary>
        /// Stops a creature whose updates have run out.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal void Tick(float unscaledDeltaTime)
        {
            if (!_hasTakenOver || _isStopped || _body == null) return;

            _sinceUpdate += unscaledDeltaTime;
            if (_sinceUpdate < StaleSeconds) return;

            try
            {
                _body.GetMovementModule()?.SetDirection(Vector2.zero);
                _isStopped = true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] stopping a creature whose updates ran out failed: " + e);
            }
        }

        /// <summary>
        /// Starts the swing the host says this creature is making, once per swing.
        ///
        /// The sequence is what tells a new swing from the one already playing, and it wraps - so the
        /// comparison is the same wrap-aware one the player stream uses rather than a greater-than.
        /// </summary>
        private void ApplyAction(in EnemySnapshotMessage.Record record)
        {
            if (record.Action == EnemySnapshotMessage.NoAction) return;

            if (_hasAppliedAction && !PlayerStateMessage.IsNewerAction(record.ActionSequence, _appliedActionSequence))
            {
                return;
            }

            Actor.ActionModule actions = _body.GetActionModule();
            if (actions == null) return;

            _appliedActionSequence = record.ActionSequence;
            _hasAppliedAction = true;

            // Requested, not forced. The module refuses while the creature is stunned, dead or mid-stance,
            // and those refusals are correct - the far copy is in a state this one is not, and a swing
            // forced through would play over an animation the body is already committed to.
            actions.TryStartAction((Actor.ActionModule.EActionBind)record.Action);
        }

        private static Vector2 Angle(byte packed)
        {
            float radians = packed / 256f * Mathf.PI * 2f;

            return new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
        }

        private static Actor.EMoveMode ToMoveMode(byte value)
        {
            return value switch
            {
                1 => Actor.EMoveMode.Walk,
                2 => Actor.EMoveMode.Run,
                _ => Actor.EMoveMode.Stand,
            };
        }
    }
}
