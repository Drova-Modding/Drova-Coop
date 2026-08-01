using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Sends where the creatures the host drives are and what they are doing.
    ///
    /// Tiered by distance and by whether anything is happening. A creature in a fight nearby is worth
    /// thirty updates a second because a swing has to land at the right moment on both screens; one
    /// wandering at the edge of the claimed range is worth fifteen, and one further than that is worth
    /// none - the recipient is about to swap its body out for a placeholder anyway.
    ///
    /// Batched into one message per tick rather than one per creature, and filled close tier first so that
    /// a batch which does not fit drops the creatures nobody is looking at.
    /// </summary>
    internal static class EnemyBroadcaster
    {
        /// <summary>How often a creature in a nearby fight is sent.</summary>
        private const float CombatInterval = 1f / 30f;

        /// <summary>How often everything else within range is sent.</summary>
        private const float IdleInterval = 1f / 15f;

        /// <summary>
        /// How close a creature has to be to a player to be sent at the faster rate. The game's own
        /// in-vision distance, which is also where it starts running its AI every frame.
        /// </summary>
        private const float CloseRange = 400f;

        private static readonly List<EnemySnapshotMessage.Record> _records = new();
        private static readonly List<Vector2> _playerPositions = new();
        private static readonly Dictionary<EnemyNetId, byte> _actionSequences = new();
        private static readonly Dictionary<EnemyNetId, byte> _lastActions = new();

        private static float _sinceCombat;
        private static float _sinceIdle;
        private static EnemySnapshotMessage _message;

        /// <summary>
        /// Sends what has changed. Call once per frame from a loaded world on the host.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting)
            {
                Clear();
                return;
            }

            _sinceCombat += unscaledDeltaTime;
            _sinceIdle += unscaledDeltaTime;

            bool sendClose = _sinceCombat >= CombatInterval;
            bool sendFar = _sinceIdle >= IdleInterval;

            if (!sendClose && !sendFar) return;

            try
            {
                Collect(sendFar);

                if (_records.Count > 0)
                {
                    Send();
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] broadcasting creature positions failed: " + e);
            }

            // The overshoot is carried rather than dropped, so the beat averages the interval instead of
            // rounding up to whole frames. At sixty frames a second the close interval is two frames to
            // within a rounding error, so zeroing here made it alternate between two and three - the same
            // creature moving half again as far between one pair of updates as the next, on a perfect
            // link, which the far side has to smooth away for no reason.
            if (sendClose) _sinceCombat -= CombatInterval;
            if (sendFar) _sinceIdle -= IdleInterval;

            // Except after a stall, where carrying it would fire a burst of catch-up sends describing
            // positions that have all been superseded.
            if (_sinceCombat > CombatInterval) _sinceCombat = 0f;
            if (_sinceIdle > IdleInterval) _sinceIdle = 0f;
        }

        /// <summary>Forgets everything. Called when the world goes away or a session ends.</summary>
        internal static void Clear()
        {
            _records.Clear();
            _actionSequences.Clear();
            _lastActions.Clear();
            _sinceCombat = 0f;
            _sinceIdle = 0f;
        }

        /// <summary>
        /// Fills the batch, close creatures first.
        ///
        /// Two passes rather than a sort. A batch that does not fit has to drop something, and what it
        /// dropped used to be whatever the claim set happened to iterate last - which can be the creature
        /// being fought while a dozen wandering at the edge of the range travel instead. Taking the close
        /// tier first makes the cut fall on the ones nobody is looking at, and costs one extra walk over a
        /// set that is tens of entries rather than a comparison sort of it.
        /// </summary>
        private static void Collect(bool includeFar)
        {
            _records.Clear();

            CollectPlayerPositions();
            if (_playerPositions.Count == 0) return;

            CollectTier(close: true);

            if (includeFar)
            {
                CollectTier(close: false);
            }
        }

        private static void CollectTier(bool close)
        {
            foreach (EnemyNetId id in CoopSession.ClaimedEnemies)
            {
                if (_records.Count >= EnemySnapshotMessage.MaxRecords) return;

                if (!EnemyRegistry.TryResolveOnHost(id, out Actor actor)) continue;

                // A body that has been registered but has not finished starting its modules. Resolvable by
                // guid and therefore reachable from here for several frames before it can answer where it is
                // facing - see EnemyRegistry.IsReady.
                if (!EnemyRegistry.IsReady(actor)) continue;

                // A corpse is not worth thirty updates a second. It is not going anywhere, its death has
                // already crossed on its own channel, and the far side refuses every one of these on
                // arrival - so the only thing they produce is a refusal counter that climbs at the send
                // rate, which reads as a fault rather than as two worlds agreeing.
                Health corpseCheck = actor.GetHealth();
                if (corpseCheck != null && corpseCheck.IsDead) continue;

                // Feet rather than the root transform, because the feet are what the far side writes:
                // Entity.SetPos moves FeetTransform, which is the root only when no separate one was
                // authored. Sending one and writing the other leaves a body permanently offset by the
                // difference, and the client cannot notice because it never reads the position back.
                Vector2 position = actor.GetFeetPosition();

                // A distant creature is only sent on the slower beat. A close one goes on every beat,
                // which is what makes a swing land at the same moment on both screens.
                if (IsWithin(position, CloseRange) != close) continue;

                _records.Add(Describe(id, actor, position));
            }
        }

        private static EnemySnapshotMessage.Record Describe(EnemyNetId id, Actor actor, Vector2 position)
        {
            byte flags = 0;

            // The module is never null; what it holds can be. Every actor module is a serialized
            // instance on the actor, so a null check here always passes - but DirectionModule is built in
            // MovementModule.StartModule, and until that has run the property returns null. IsReady above is
            // what keeps that from happening; this is what keeps it from being fatal if it does, because the
            // exception it throws takes every other creature in the batch with it.
            Actor.MovementModule movement = actor.GetMovementModule();
            IActorDirection direction = movement.DirectionModule;

            Vector2 moveDirection = direction != null ? direction.DirectionVector() : Vector2.zero;

            if (moveDirection.sqrMagnitude > 0.0001f)
            {
                flags |= EnemySnapshotMessage.FlagMoving;
            }

            flags |= (byte)(((byte)movement.IntendedMode & 0b11) << 1);

            if (actor.AIModule != null && actor.AIModule.IsInCombat())
            {
                flags |= EnemySnapshotMessage.FlagInCombat;
            }

            if (actor.CustomStats != null && actor.CustomStats.IsKnockout)
            {
                flags |= EnemySnapshotMessage.FlagKnockout;
            }

            Health health = actor.GetHealth();
            if (health != null && health.IsDead)
            {
                flags |= EnemySnapshotMessage.FlagDead;
            }

            Vector2 lookDirection = actor.GetLookModule().TargetLookDir;

            // Through the same reader the player stream uses, which is what makes a creature's combo
            // travel as the attack that started it rather than as no action at all - the later swings of a
            // chain are separate instances built from the combo's own prefab and match no bind.
            ActionBinds.Snapshot swing = ActionBinds.Current(actor.GetActionModule());
            byte action = ToWire(swing);

            // The half of a swing that was being thrown away. The AI starts an attack by holding the bind
            // and lets go on a timer - TryStartAction, then TryReleaseCurrentAction - and the hold is the
            // wind-up. Sending only the start left every replicated attack held, so a creature charged and
            // never struck.
            if (swing.Released)
            {
                flags |= EnemySnapshotMessage.FlagActionReleased;
            }

            return new EnemySnapshotMessage.Record
            {
                NetId = id,
                OffsetX = Quantize(position.x - _message.OriginX),
                OffsetY = Quantize(position.y - _message.OriginY),
                LookAngle = Pack(lookDirection),
                MoveAngle = Pack(moveDirection),
                Flags = flags,
                ModeAndTarget = PackMode(actor),
                Action = action,
                ActionSequence = SequenceFor(id, action),
            };
        }

        /// <summary>
        /// The creature's stance, in the low nibble of the byte the record keeps for it.
        ///
        /// Not cosmetic, and not the same thing as the combat flag beside it. That flag is the AI's own
        /// idea of being in a fight; this is the interaction mode, and the interaction mode is what the
        /// game multiplies movement speed by:
        /// <code>
        /// public float CombatSpeedFactor()
        /// {
        ///     if (_interactionModule.IsInCombatMode || _interactionModule.IsChangingMode)
        ///     { return _speedData.CombatSpeedFactor; }
        ///     return 1f;
        /// }
        /// </code>
        /// with a factor of six tenths. A creature fighting on the host and merely walking on the client
        /// therefore travels the same direction at two different speeds, so the two copies part company
        /// continuously and the correction has to keep pulling one back. It is the only error here that
        /// never settles.
        ///
        /// The target half of the byte stays unfilled - who a creature has picked is not replicated, and
        /// the far side does not act on it.
        ///
        /// The mode this asks for rather than the one it is in, matching the player stream: a stance is a
        /// transition the game plays out, and sending where it is going is what lets the far side play the
        /// same one rather than snapping to the end of it.
        /// </summary>
        private static byte PackMode(Actor actor)
        {
            Actor.InteractionModeModule stance = actor.GetInteractionModule();
            if (stance == null) return EnemySnapshotMessage.NoValue;

            byte mode = (byte)stance.TargetMode;

            // A mode this build numbers past the nibble is sent as none rather than as a different mode.
            if (mode >= EnemySnapshotMessage.NoValue) return EnemySnapshotMessage.NoValue;

            return (byte)(mode | (EnemySnapshotMessage.NoValue << 4));
        }

        private static byte ToWire(in ActionBinds.Snapshot snapshot)
        {
            return snapshot.Bind == Actor.ActionModule.EActionBind.None
                ? EnemySnapshotMessage.NoAction
                : (byte)snapshot.Bind;
        }

        /// <summary>
        /// Counts a creature's swings, so the far side can tell a new one from the one it is playing.
        ///
        /// Bumped when the action changes rather than every frame, because a sequence that moved
        /// constantly would restart the animation on every packet.
        /// </summary>
        private static byte SequenceFor(EnemyNetId id, byte action)
        {
            _lastActions.TryGetValue(id, out byte last);
            _actionSequences.TryGetValue(id, out byte sequence);

            if (action != last)
            {
                sequence = unchecked((byte)(sequence + 1));

                _lastActions[id] = action;
                _actionSequences[id] = sequence;
            }

            return sequence;
        }

        private static void Send()
        {
            // The origin is whatever the first creature in the batch is near, so the surrounding offsets are
            // small. Everything in a batch is within a few hundred units of a player by construction.
            _message.Records = null;
            _message.Count = 0;

            EnemySnapshotMessage message = _message with
            {
                Records = [.. _records],
                Count = _records.Count
            };

            CoopSession.BroadcastEnemySnapshot(message);
        }

        private static void CollectPlayerPositions()
        {
            _playerPositions.Clear();

            if (PlayerAccess.TryGetPlayer(out Actor local) && local != null)
            {
                Vector2 position = local.transform.position;

                _playerPositions.Add(position);

                // The origin rides with the host's own player, which keeps the offsets small for the
                // creatures most likely to be in the batch and makes it stable between ticks.
                _message.OriginX = position.x;
                _message.OriginY = position.y;
            }

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                Actor actor = body.Value.Body;
                if (actor != null)
                {
                    _playerPositions.Add(actor.transform.position);
                }
            }
        }

        private static bool IsWithin(Vector2 position, float range)
        {
            float squared = range * range;

            foreach (Vector2 player in _playerPositions)
            {
                if ((player - position).sqrMagnitude <= squared) return true;
            }

            return false;
        }

        private static short Quantize(float offset)
        {
            float steps = offset / EnemySnapshotMessage.PositionStep;

            if (steps >= short.MaxValue) return short.MaxValue;
            if (steps <= short.MinValue) return short.MinValue;

            return (short)Mathf.RoundToInt(steps);
        }

        private static byte Pack(Vector2 direction)
        {
            if (direction.sqrMagnitude < 0.0001f) return 0;

            float turns = Mathf.Atan2(direction.y, direction.x) / (Mathf.PI * 2f);
            if (turns < 0f) turns += 1f;

            return (byte)(Mathf.RoundToInt(turns * 256f) & 0xFF);
        }
    }
}
