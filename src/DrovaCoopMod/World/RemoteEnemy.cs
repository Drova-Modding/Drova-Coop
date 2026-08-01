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
        /// Not optional, and not a tidiness measure. A creature whose AI has been switched off keeps
        /// moving in whatever direction it was last given, forever - the movement module takes its
        /// move-with-direction branch precisely when the AI is inactive. So a creature that leaves the
        /// host's interest set, or whose updates are lost, walks away across the map until something stops
        /// it. This is what stops it.
        /// </summary>
        private const float StaleSeconds = 0.5f;

        /// <summary>
        /// How quickly a driven creature is pulled onto the position the host reports.
        ///
        /// A rate rather than a step, applied as <c>1 - e^(-rate * dt)</c>, so the same fraction of the
        /// remaining error closes per second whatever the frame rate. Thirty is a time constant of
        /// thirty-three milliseconds, which retires sixty-three percent of an error within the gap between
        /// two updates at the faster send rate and eighty-six percent at the slower one.
        ///
        /// Twelve did not. One over twelve is eighty-three milliseconds, which is longer than either gap
        /// rather than shorter - so a third of each error was retired before the next update measured what
        /// was left, and the body never finished catching up.
        ///
        /// Not much higher than this. The reported position carries the jitter of the link, and a
        /// corrector faster than the beat feeding it reproduces that jitter rather than absorbing it.
        /// </summary>
        private const float CorrectionRate = 30f;

        /// <summary>
        /// The error past which the body is put where it belongs instead of walked there.
        ///
        /// Sliding is only right for the small disagreement two copies of the same walk accumulate. A
        /// creature that has teleported, that has just changed hands, or whose copy here has been running
        /// its own AI somewhere else entirely is not disagreeing - it is somewhere else, and sliding it
        /// across the intervening ground would be a creature visibly gliding through the scenery.
        /// </summary>
        private const float SnapDistance = 60f;

        /// <summary>
        /// Below this the error is dropped rather than chased.
        ///
        /// The wire quantizes position to a quarter of a unit, so a body that has arrived still reports an
        /// error of up to half that. Chasing it forever would write a transform every frame for every
        /// driven creature to move it nowhere.
        /// </summary>
        private const float SettledDistance = 0.25f;

        private readonly Actor _body;
        private readonly Rigidbody2D _rigidbody;

        private bool _hasTakenOver;
        private float _sinceUpdate;
        private bool _isStopped;
        private byte _appliedActionSequence;
        private bool _hasAppliedAction;
        private Actor.ActionModule.EActionBind _heldAction = Actor.ActionModule.EActionBind.None;
        private bool _releaseApplied;
        private bool _hasAppliedStance;
        private Vector2 _error;
        private bool _hasError;

        /// <summary>
        /// Wraps a body the host has claimed the driving of.
        /// </summary>
        /// <param name="body">The creature.</param>
        internal RemoteEnemy(Actor body)
        {
            _body = body;

            // Held rather than looked up per frame, and safe to hold: a body that is replaced gets a new
            // RemoteEnemy rather than being swapped into this one.
            _rigidbody = body != null ? body.GetComponent<Rigidbody2D>() : null;
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
        /// Reset before reactivate. A behavior tree resumes whatever it was doing, and what it was
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

                // Dropped rather than finished. What is left of it describes where the host last wanted a
                // creature that is now steering itself, and writing that onto a body its own AI is moving
                // would be two hands on it at once.
                _error = Vector2.zero;
                _hasError = false;

                // So a body taken over again later starts its stance from where it actually is rather than
                // from where the last hand-over left this.
                _hasAppliedStance = false;
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
                    Correct(movement, position);

                    bool isMoving = (record.Flags & EnemySnapshotMessage.FlagMoving) != 0;

                    movement.SetDirection(isMoving ? Angle(record.MoveAngle) : Vector2.zero);
                    movement.SetIntendedMode(ToMoveMode((byte)((record.Flags >> 1) & 0b11)));
                }

                Actor.LookModule look = _body.GetLookModule();
                if (look != null)
                {
                    look.SetLookDirection(Angle(record.LookAngle));
                }

                ApplyStance(record.ModeAndTarget);
                ApplyAction(record);
                ApplyRelease(record);
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
            if (!_hasTakenOver || _body == null) return;

            Ease(unscaledDeltaTime);

            if (_isStopped) return;

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
        /// Puts the body into the stance the host says it is in.
        ///
        /// What this is really for is the speed. The game multiplies movement by a combat factor of six
        /// tenths whenever the interaction module is in or entering combat mode, so a creature fighting on
        /// the host and merely standing here walks the same direction half again as fast - a disagreement
        /// that grows for as long as the fight lasts and that no amount of position correction settles,
        /// because it is regenerated between every pair of updates.
        ///
        /// Asked for rather than set once the body is in step, for the reason the player stream gives: the
        /// change plays its own transition, and both copies playing it is what keeps them together.
        ///
        /// The first one is set, and that exception is the whole difficulty. A transition is not a
        /// dressing-up animation, it is a stop:
        /// <code>
        /// public float ComputeMoveSpeed()
        /// {
        ///     ...
        ///     if (!_interactionModule.IsMovementAllowed()) { num *= 0f; }
        /// }
        ///
        /// public bool IsMovementAllowed()
        /// {
        ///     ...
        ///     if (_currentMode == EInteractionMode.Normal &amp;&amp; _targetMode == EInteractionMode.Combat) return false;
        ///     if (_currentMode == EInteractionMode.Combat &amp;&amp; _targetMode == EInteractionMode.Normal) return false;
        /// }
        /// </code>
        /// so a creature drawing or putting away its weapon cannot walk for the whole of it - a second, on
        /// a mode the prefab did not author - and <c>IsChangingMode</c> refuses every action started during
        /// it besides. That is correct for a creature doing it here and now, and it is exactly wrong for a
        /// creature that is only being told the answer: the host played that transition already, its copy
        /// keeps walking throughout, and this one stands still until the gap passes
        /// <see cref="SnapDistance"/> and is closed in one frame. Which is what a stall of about a second
        /// ending in a jump across the ground is.
        ///
        /// It bites hardest in the case that looks least like it should. A creature the client's own AI has
        /// been chasing somebody with is in combat here and, on a host too far away to have an opinion
        /// about it, in nothing at all there - so the first update after the host takes it over asks it to
        /// put its weapon away, and the stall lands the moment the creature stops being the client's.
        ///
        /// Compared against the queued mode rather than the target one, because the queued mode is what
        /// <c>RequestMode</c> writes. <c>TargetMode</c> only catches up when <c>UpdateModule</c> begins the
        /// change, which it declines to do while one is already running or while the current action blocks
        /// it - so comparing against that asks again on every frame of exactly the window where asking
        /// again does harm.
        /// </summary>
        private void ApplyStance(byte modeAndTarget)
        {
            byte value = (byte)(modeAndTarget & 0x0F);
            if (value == EnemySnapshotMessage.NoValue) return;

            Actor.EInteractionMode wanted = (Actor.EInteractionMode)value;

            // A stance this build does not know would otherwise be requested as whatever it numbers here.
            if (!Enum.IsDefined(typeof(Actor.EInteractionMode), wanted)) return;

            Actor.InteractionModeModule stance = _body.GetInteractionModule();
            if (stance == null) return;

            if (!_hasAppliedStance)
            {
                _hasAppliedStance = true;

                // Set through the game's own instant path, which requests the mode and then finishes it in
                // the same call, so nothing is ever left mid-change: SetModeImmediate assigns both the
                // current and the target mode and zeroes the change timer.
                if (stance.CurrentMode != wanted || stance.IsChangingMode)
                {
                    stance.SetModeInstant(wanted);
                }

                return;
            }

            if (stance.QueuedMode == wanted) return;

            stance.RequestMode(wanted);
        }

        /// <summary>
        /// Puts the body somewhere, through both of the positions it keeps.
        ///
        /// The movement module only writes one of them. <c>SetPosition</c> is <c>Entity.SetPos</c>, which
        /// is one call to <c>FeetTransform.SetPos2</c> and nothing else - while the walk is driven
        /// entirely from the rigidbody:
        /// <code>
        /// Vector2 position = _rigidbody.position;
        /// ...
        /// Vector2 position2 = _rigidbody.position;
        /// _rigidbody.position = position;
        /// _rigidbody.MovePosition(position2);
        /// </code>
        /// Nothing in the game sets <c>Physics2D.autoSyncTransforms</c>, and Unity leaves it off, so a
        /// transform write never reaches the rigidbody. The body is therefore moved and then carried
        /// straight back on the next physics step, which is a displacement per write and an error that
        /// never retires however gently it is applied.
        ///
        /// So both, which is what the game itself does when it means it:
        /// <code>
        /// FeetTransform.SetPos2(pos);
        /// if ((bool)_rigidbody2D) { _rigidbody2D.position = pos; }
        /// </code>
        /// </summary>
        private void Place(Actor.MovementModule movement, Vector2 where)
        {
            movement.SetPosition(where);

            if (_rigidbody != null)
            {
                _rigidbody.position = where;
            }
        }

        /// <summary>
        /// Takes in where the host says this creature is, without putting it there.
        ///
        /// A driven creature walks itself. Switching its AI off is what hands it to the wire, and the
        /// movement module reads that as permission to move under its own steam:
        /// <code>
        /// if (Enabled &amp;&amp; _entityRigidbody &amp;&amp; (_movementTarget.IsActive || !_actor.AIModule.IsAIActiveAndValid() || ...))
        /// { ... _movementActor.MoveWithDirection(dt); }
        /// </code>
        /// So between two updates the body is already travelling in the direction the last one gave it,
        /// smoothly and at its own speed. Writing the reported position on top of that, fifteen to thirty
        /// times a second, is what the choppiness was: the walk and the correction disagree by a little
        /// every time, and every disagreement is paid off in a single frame.
        ///
        /// So the disagreement is recorded here and paid off over the following frames instead. The walk
        /// keeps producing the motion; this only removes the drift.
        /// </summary>
        private void Correct(Actor.MovementModule movement, Vector2 authoritative)
        {
            Vector2 here = _body.GetFeetPosition();
            Vector2 gap = authoritative - here;

            if (gap.sqrMagnitude > SnapDistance * SnapDistance)
            {
                Place(movement, authoritative);

                _error = Vector2.zero;
                _hasError = false;
                return;
            }

            // Assigned rather than accumulated: whatever was left of the last one has already moved the
            // body, so it is part of the position this was just measured against.
            _error = gap;
            _hasError = gap.sqrMagnitude > SettledDistance * SettledDistance;
        }

        /// <summary>
        /// Pays off a little of the outstanding error, once per frame.
        ///
        /// Measured against the body's live position each time rather than against a target computed when
        /// the update arrived, so the distance the creature walked itself in the meantime is kept and only
        /// what it got wrong is taken back.
        /// </summary>
        private void Ease(float unscaledDeltaTime)
        {
            if (!_hasError) return;

            try
            {
                Actor.MovementModule movement = _body.GetMovementModule();
                if (movement == null)
                {
                    _hasError = false;
                    return;
                }

                Vector2 step = _error * (1f - Mathf.Exp(-CorrectionRate * unscaledDeltaTime));

                Place(movement, _body.GetFeetPosition() + step);

                _error -= step;
                _hasError = _error.sqrMagnitude > SettledDistance * SettledDistance;
            }
            catch (Exception e)
            {
                _hasError = false;
                MelonLogger.Error("[Enemy] settling a driven creature onto its reported position failed: " + e);
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
            Actor.ActionModule.EActionBind bind = (Actor.ActionModule.EActionBind)record.Action;

            actions.TryStartAction(bind);

            _heldAction = bind;
            _releaseApplied = false;
        }

        /// <summary>
        /// Lets go of the swing when the host has, which is what makes a wind-up end in a blow.
        ///
        /// The AI attacks by holding the bind and releasing it on a timer:
        /// <code>
        /// _actionModule.TryStartAction(actionBind);      // ... then, holdTime later
        /// _actionModule.TryReleaseCurrentAction();
        /// </code>
        /// and the hold is the charge - the wind-up, and the warning drawn over it. This used to start the
        /// action and never let go, so every replicated attack was held: the creature wound up and the
        /// blow never arrived.
        ///
        /// Driven by the flag on every update rather than by the moment it changed, so a lost packet costs
        /// nothing - the next one still says the button is up. Applied once per swing, because the game
        /// ignores a repeat and there is no reason to make the call.
        /// </summary>
        private void ApplyRelease(in EnemySnapshotMessage.Record record)
        {
            if (_releaseApplied) return;
            if (_heldAction == Actor.ActionModule.EActionBind.None) return;
            if ((record.Flags & EnemySnapshotMessage.FlagActionReleased) == 0) return;

            Actor.ActionModule actions = _body.GetActionModule();
            if (actions == null) return;

            actions.TryReleaseAction(_heldAction);
            _releaseApplied = true;
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
