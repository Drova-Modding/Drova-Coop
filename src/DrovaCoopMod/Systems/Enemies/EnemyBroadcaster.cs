using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Sends where the host's creatures are and what they are doing, one batch per tick: 30 Hz near a
    /// player, 15 Hz further out, close tier first so an overfull batch drops the distant ones.
    /// </summary>
    internal sealed class EnemyBroadcaster : ICoopSystem
    {
        /// <summary>How often a creature in a nearby fight is sent.</summary>
        private const float CombatInterval = 1f / 30f;

        /// <summary>How often everything else within range is sent.</summary>
        private const float IdleInterval = 1f / 15f;

        /// <summary>
        /// How close a creature has to be to a player to be sent at the faster rate. The game's in-vision distance.
        /// </summary>
        private const float CloseRange = 400f;

        /// <summary>
        /// How often an unchanged creature is sent anyway. Inside RemoteEnemy.StaleSeconds, so a lost last update still lands.
        /// </summary>
        private const float KeepaliveSeconds = 0.4f;

        private static readonly List<EnemySnapshotMessage.Record> RecordBuffer = new();
        private static readonly EnemySnapshotMessage.Record[] SendRecords = new EnemySnapshotMessage.Record[EnemySnapshotMessage.MaxRecords];

        // ClaimedIds copied for indexing, walked from _cursor so a full batch does not starve the ones after it.
        private static readonly List<EnemyNetId> Order = new();
        private static readonly Dictionary<EnemyNetId, LastSwing> LastSwings = new();

        private static readonly Dictionary<IntPtr, uint> PrefabHashes = new();

        private static readonly Dictionary<EnemyNetId, (EnemySnapshotMessage.Record Record, Vector2 Position, float At)> LastSent = new();

        /// <summary>
        /// One creature's last observed swing and the sequence number the far side knows it by.
        /// </summary>
        private readonly struct LastSwing(ActionBinds.Snapshot swing, uint hash, byte sequence)
        {
            internal ActionBinds.Snapshot Swing { get; } = swing;

            internal uint Hash { get; } = hash;

            internal byte Sequence { get; } = sequence;
        }

        private static float _sinceCombat;
        private static float _sinceIdle;
        private static int _cursor;
        private static EnemySnapshotMessage _message;

        // Whether the host's own player was found this tick. Its position is the message origin.
        private static bool _hasHost;

        /// <summary>Sends what has changed.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting)
            {
                // Kept while joined, since LeaseMirror uses the same swing memory for leased fighters.
                if (CoopSession.State != CoopSessionState.Joined) Clear();
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

                if (RecordBuffer.Count > 0)
                {
                    Send();
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] broadcasting creature positions failed: " + e);
            }

            // The overshoot is carried so the beat averages the interval, except after a stall.
            if (sendClose) _sinceCombat -= CombatInterval;
            if (sendFar) _sinceIdle -= IdleInterval;

            if (_sinceCombat > CombatInterval) _sinceCombat = 0f;
            if (_sinceIdle > IdleInterval) _sinceIdle = 0f;
        }

        /// <summary>Forgets everything. Called when the world goes away or a session ends.</summary>
        internal static void Clear()
        {
            RecordBuffer.Clear();
            Order.Clear();
            LastSwings.Clear();
            PrefabHashes.Clear();
            LastSent.Clear();
            _sinceCombat = 0f;
            _sinceIdle = 0f;
        }

        /// <summary>
        /// Fills the batch, close creatures first.
        /// </summary>
        private static void Collect(bool includeFar)
        {
            RecordBuffer.Clear();

            _hasHost = PlayerAccess.TryGetPlayer(out Actor? local);

            if (local != null)
            {
                // The origin is the host's player, keeping offsets small.
                Vector2 host = local.transform.position;
                _message.OriginX = host.x;
                _message.OriginY = host.y;
            }
            else if (RemotePlayers.Positions.Count == 0)
            {
                return;
            }

            Order.Clear();
            Order.AddRange(EnemyRegistry.ClaimedIds);

            CollectTier(close: true);

            if (includeFar)
            {
                CollectTier(close: false);
            }
        }

        private static void CollectTier(bool close)
        {
            int count = Order.Count;
            int start = count == 0 ? 0 : _cursor % count;

            for (int step = 0; step < count; step++)
            {
                int index = (start + step) % count;

                // The next tick starts at the first creature this one had no room for.
                if (RecordBuffer.Count >= EnemySnapshotMessage.MaxRecords)
                {
                    _cursor = index;
                    return;
                }

                EnemyNetId id = Order[index];

                if (!EnemyRegistry.TryResolveOnHost(id, out Actor? actor)) continue;

                if (!EnemyRegistry.IsReady(actor)) continue;

                // Deaths cross on their own channel, and the far side refuses corpse snapshots.
                Health? corpseCheck = actor.GetHealth();
                if (corpseCheck != null && corpseCheck.IsDead) continue;

                // An NPC held for a client's dialogue is left to it, unless the scene walks it
                // (CutscenePorts.ReportWalk), since the client's copy cannot walk itself.
                bool held = EnemyEngagements.IsHeld(id);

                // Feet, because the far side writes them with Entity.SetPos, which moves FeetTransform.
                Vector2 position = actor.GetFeetPosition();

                if (IsWithin(position, CloseRange) != close) continue;

                EnemySnapshotMessage.Record record = Describe(id, actor, position, new Vector2(_message.OriginX, _message.OriginY));
                if (held && (record.Flags & EnemySnapshotMessage.FlagMoving) == 0) continue;
                if (IsUnchanged(id, record, position)) continue;

                RecordBuffer.Add(record);
            }
        }

        /// <summary>
        /// Whether nothing changed since the last send and the keepalive is not due. Offsets move with the host, so positions are compared instead.
        /// </summary>
        private static bool IsUnchanged(EnemyNetId id, in EnemySnapshotMessage.Record record, Vector2 position)
        {
            float now = Time.unscaledTime;

            if ((record.Flags & EnemySnapshotMessage.FlagMoving) == 0
                && LastSent.TryGetValue(id, out (EnemySnapshotMessage.Record Record, Vector2 Position, float At) last)
                && now - last.At < KeepaliveSeconds
                && (position - last.Position).sqrMagnitude < 1f
                && record.LookAngle == last.Record.LookAngle && record.MoveAngle == last.Record.MoveAngle
                && record.Flags == last.Record.Flags && record.ModeAndTarget == last.Record.ModeAndTarget
                && record.Action == last.Record.Action && record.ActionSequence == last.Record.ActionSequence
                && record.ActionHash == last.Record.ActionHash)
            {
                return true;
            }

            LastSent[id] = (record, position, now);
            return false;
        }

        /// <summary>A creature as one record, its position as an offset from <paramref name="origin"/>. Also for LeaseMirror.</summary>
        internal static EnemySnapshotMessage.Record Describe(EnemyNetId id, Actor actor, Vector2 position, Vector2 origin)
        {
            byte flags = 0;

            // DirectionModule is null until MovementModule.StartModule runs.
            Actor.MovementModule movement = actor.GetMovementModule();
            IActorDirection? direction = movement.DirectionModule;

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

            Health? health = actor.GetHealth();
            if (health != null && health.IsDead)
            {
                flags |= EnemySnapshotMessage.FlagDead;
            }

            if (actor.TorchTimer != null && actor.TorchTimer.IsUsingTorch)
            {
                flags |= EnemySnapshotMessage.FlagTorch;
            }

            Vector2 lookDirection = actor.GetLookModule().TargetLookDir;

            // The prefab hash crosses too, since the far copy never ran the AI equip that installs the bind.
            ActionBinds.Snapshot swing = ActionBinds.Observe(actor.GetActionModule(), out Il2CppDrova.ActorActions.AA_ABase? rootPrefab);
            byte action = ToWire(swing);
            uint actionHash = HashOf(rootPrefab);

            // The AI holds the bind for the wind-up and releases on a timer, so the release must cross too.
            if (swing.Released)
            {
                flags |= EnemySnapshotMessage.FlagActionReleased;
            }

            return new EnemySnapshotMessage.Record
            {
                NetId = id,
                OffsetX = Quantize(position.x - origin.x),
                OffsetY = Quantize(position.y - origin.y),
                LookAngle = Pack(lookDirection),
                MoveAngle = Pack(moveDirection),
                Flags = flags,
                ModeAndTarget = PackMode(actor),
                Action = action,
                ActionSequence = SequenceFor(id, actionHash, swing),
                ActionHash = actionHash,
            };
        }

        /// <summary>
        /// The wire name of what a swing was built from, cached per prefab.
        /// </summary>
        private static uint HashOf(Il2CppDrova.ActorActions.AA_ABase? prefab)
        {
            if (prefab == null) return 0;

            if (PrefabHashes.TryGetValue(prefab.Pointer, out uint hash)) return hash;

            hash = ActionPrefabIndex.Hash(prefab.name);
            PrefabHashes[prefab.Pointer] = hash;

            return hash;
        }

        /// <summary>
        /// The creature's target interaction mode in the low nibble. It sets the combat speed factor, so a
        /// mismatch makes the two copies drift apart. Bit four says it is stunned.
        /// </summary>
        private static byte PackMode(Actor actor)
        {
            // A stun is no action: the poise-break behaviours play it, so the far copy breaks its own poise.
            byte stunned = actor.PoiseController != null && actor.PoiseController.TryGetPoiseBreak(out _) ? EnemySnapshotMessage.PoiseBroken : (byte)0;

            Actor.InteractionModeModule? stance = actor.GetInteractionModule();
            byte mode = stance == null ? EnemySnapshotMessage.NoValue : (byte)stance.TargetMode;

            // A mode this build numbers past the nibble is sent as none rather than as a different mode.
            if (mode > EnemySnapshotMessage.NoValue) mode = EnemySnapshotMessage.NoValue;

            return (byte)(mode | stunned);
        }

        private static byte ToWire(in ActionBinds.Snapshot snapshot)
        {
            return snapshot.Bind == Actor.ActionModule.EActionBind.None
                ? EnemySnapshotMessage.NoAction
                : (byte)snapshot.Bind;
        }

        /// <summary>
        /// Counts a creature's swings by the snapshot, not the wire bytes, which stay the same through a combo.
        /// </summary>
        private static byte SequenceFor(EnemyNetId id, uint actionHash, in ActionBinds.Snapshot swing)
        {
            bool known = LastSwings.TryGetValue(id, out LastSwing last);
            byte sequence = last.Sequence;

            if (!known || !swing.IsSameUse(last.Swing) || actionHash != last.Hash)
            {
                // The first sight is a baseline, or the far side restarts on every entry into the batch.
                if (known)
                {
                    sequence = unchecked((byte)(sequence + 1));
                }

                LastSwings[id] = new LastSwing(swing, actionHash, sequence);
            }

            return sequence;
        }

        private static void Send()
        {
            RecordBuffer.CopyTo(SendRecords);

            EnemySnapshotMessage message = _message with
            {
                Records = SendRecords,
                Count = RecordBuffer.Count
            };

            RemoteEnemies.BroadcastEnemySnapshot(message);
        }

        private static bool IsWithin(Vector2 position, float range)
        {
            Vector2 host = new(_message.OriginX, _message.OriginY);

            return (_hasHost && (host - position).sqrMagnitude <= range * range)
                || RemotePlayers.IsAnyWithin(position, range);
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
            PlayerStateMessage.TryPackAngle(direction.x, direction.y, out byte angle);
            return angle;
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Clear();
        }
    }
}
