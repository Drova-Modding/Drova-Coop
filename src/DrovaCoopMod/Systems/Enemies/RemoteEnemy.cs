using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// One creature the host drives, seen from a client. A stripped <see cref="RemotePlayer"/>.
    /// </summary>
    internal sealed class RemoteEnemy
    {
        /// <summary>
        /// How long a creature is driven after its last update before it is stopped, since with its AI off
        /// it keeps walking in the last direction forever.
        /// </summary>
        private const float StaleSeconds = 0.5f;

        /// <summary>
        /// How quickly the body is pulled onto the reported position, applied as <c>1 - e^(-rate * dt)</c>.
        /// A 33 ms time constant, shorter than the update gap but not so fast it reproduces link jitter.
        /// </summary>
        private const float CorrectionRate = 30f;

        /// <summary>The error past which the body is placed instead of slid there.</summary>
        private const float SnapDistance = 60f;

        /// <summary>Below this the error is dropped, since the wire quantizes position to a quarter unit.</summary>
        private const float SettledDistance = 0.25f;

        /// <summary>How long the host and this copy must disagree about an action before this copy is corrected.</summary>
        private const float DisagreeSeconds = 1.5f;

        private readonly Actor _body;
        private readonly Rigidbody2D? _rigidbody;

        private bool _hasTakenOver;
        private float _sinceUpdate;
        private bool _isStopped;
        private byte _appliedActionSequence;
        private bool _hasAppliedAction;
        private Actor.ActionModule.EActionBind _heldAction = Actor.ActionModule.EActionBind.None;
        private Il2CppDrova.ActorActions.AA_ABase? _heldPrefab;
        private bool _releaseApplied;

        // See SettleIdle.
        private float _idleSince = -1f;

        // See KeepPlaying.
        private float _lostSince = -1f;

        private bool _hasAppliedStance;
        private bool _appliedInCombat;
        private Il2CppDrova.Brawl.BrawlActor? _knockout;
        private Vector2 _error;
        private bool _hasError;

        /// <summary>How many long actions were started again because the host was still playing them. For tests.</summary>
        internal static int KeptPlaying { get; private set; }

        /// <summary>How many leftover actions were ended because the host had stopped. For tests.</summary>
        internal static int IdleCleared { get; private set; }

        /// <summary>Whether the wire is steering this body right now, its own AI switched off. For diagnostics.</summary>
        internal bool IsTakenOver => _hasTakenOver;

        /// <summary>The body being driven.</summary>
        internal Actor Body => _body;

        /// <summary>Wraps a body the host has claimed the driving of.</summary>
        internal RemoteEnemy(Actor body)
        {
            _body = body;

            // A replaced body gets a new RemoteEnemy, so this is safe to hold.
            _rigidbody = body != null ? body.GetComponent<Rigidbody2D>() : null;
        }

        /// <summary>
        /// Forgets which swing was last applied, so the next record is a baseline. For a sleeping body,
        /// whose wrapping swing counter could otherwise compare every later swing as the past.
        /// </summary>
        internal void ForgetSwing()
        {
            _hasAppliedAction = false;
            _heldPrefab = null;
            _heldAction = Actor.ActionModule.EActionBind.None;
            _releaseApplied = false;
        }

        /// <summary>Puts this machine's copy under the wire's control. Zeroing the direction keeps the AI-less body from sliding away.</summary>
        private void TakeOver()
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

        /// <summary>Gives the creature back to its own AI, reset first so it does not resume a minutes-old errand.</summary>
        internal void HandBack()
        {
            if (!_hasTakenOver || _body == null) return;

            try
            {
                _body.GetMovementModule().SetDirection(Vector2.zero);

                StandUp();

                // By hand, since ResetAI is a no-op on a deactivated AI and the combat flag would stay set.
                if (_appliedInCombat)
                {
                    _body.AIModule.SetInCombat(false, false);
                    _appliedInCombat = false;
                }

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
                _error = Vector2.zero;
                _hasError = false;
                _hasAppliedStance = false;
            }
        }

        /// <summary>Puts one update from the host onto this body.</summary>
        internal void Apply(in EnemySnapshotMessage.Record record, Vector2 origin)
        {
            if (_body == null) return;

            TakeOver();

            _sinceUpdate = 0f;
            _isStopped = false;

            try
            {
                // Re-asserted, since take-over can be silently refused and the game can reactivate the AI.
                if (_hasTakenOver && _body.AIModule != null && _body.AIModule.IsAIActiveAndValid())
                {
                    _body.AIModule.DeactivateAI();
                }

                Vector2 position = origin + new Vector2(
                    record.OffsetX * EnemySnapshotMessage.PositionStep,
                    record.OffsetY * EnemySnapshotMessage.PositionStep);

                Actor.MovementModule? movement = _body.GetMovementModule();
                if (movement != null)
                {
                    Correct(movement, position);

                    bool isMoving = (record.Flags & EnemySnapshotMessage.FlagMoving) != 0;

                    movement.SetDirection(isMoving ? Angle(record.MoveAngle) : Vector2.zero);
                    movement.SetIntendedMode(ToMoveMode((byte)((record.Flags >> 1) & 0b11)));
                }

                Actor.LookModule? look = _body.GetLookModule();
                if (look != null)
                {
                    look.SetLookDirection(Angle(record.LookAngle));
                }

                ApplyStance(record.ModeAndTarget);
                ApplyCombatState(record.Flags);
                ApplyKnockout(record.Flags);
                ApplyTorch(record.Flags);
                ApplyAction(record);
                ApplyRelease(record);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] driving a creature failed: " + e);
            }
        }

        /// <summary>Eases the position error and stops a creature whose updates have run out.</summary>
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
        /// Puts the body into the host's stance, which sets the combat move-speed factor.
        /// The first one is set instantly, since a mode transition blocks movement for about a second.
        /// Later ones are requested, compared against <c>QueuedMode</c>, which is what <c>RequestMode</c> writes.
        /// </summary>
        private void ApplyStance(byte modeAndTarget)
        {
            byte value = (byte)(modeAndTarget & 0x0F);
            if (value == EnemySnapshotMessage.NoValue) return;

            Actor.EInteractionMode wanted = (Actor.EInteractionMode)value;

            if (!Enum.IsDefined(typeof(Actor.EInteractionMode), wanted)) return;

            Actor.InteractionModeModule? stance = _body.GetInteractionModule();
            if (stance == null) return;

            if (!_hasAppliedStance)
            {
                _hasAppliedStance = true;

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
        /// Puts the body somewhere, transform and rigidbody both, since <c>SetPosition</c> only writes the
        /// transform and the walk is driven from the rigidbody without auto-sync.
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
        /// Records the gap to the host's position for <see cref="Ease"/>, since an AI-less body already walks
        /// its last direction and snapping each update looks choppy.
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

            // Assigned, not accumulated, since the last one already moved the body.
            _error = gap;
            _hasError = gap.sqrMagnitude > SettledDistance * SettledDistance;
        }

        /// <summary>Pays off a little of the outstanding error from the live position, once per frame.</summary>
        private void Ease(float unscaledDeltaTime)
        {
            if (!_hasError) return;

            try
            {
                Actor.MovementModule? movement = _body.GetMovementModule();
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
        /// Mirrors the creature's combat flag and flashes its aggro marker, both normally set by the AI a driven body has off.
        /// Edge-triggered and without group propagation, since the host decides per creature.
        /// </summary>
        private void ApplyCombatState(byte flags)
        {
            bool fighting = (flags & EnemySnapshotMessage.FlagInCombat) != 0;
            if (fighting == _appliedInCombat) return;

            IActorAIModule? ai = _body.AIModule;
            if (ai == null) return;

            ai.SetInCombat(fighting, false);
            _appliedInCombat = fighting;

            // The marker module caches an empty sprite for good if shown before its pool has filled it.
            AIActorModule? module = ai.TryCast<AIActorModule>();
            if (module == null || module._aggroSymbolModule == null || module._aggroSymbolModule._bhvr == null) return;

            // Zero duration is the game's own one-shot: the symbol hides itself when its animation ends.
            ai.ToggleAggroSymbol(
                fighting ? Il2CppDrova.AI.AggroSymbolState.Aggro : Il2CppDrova.AI.AggroSymbolState.LooseAggro,
                0f);
        }

        /// <summary>Takes up or puts away the torch the host's copy holds, the way the game's routine service does.</summary>
        private void ApplyTorch(byte flags)
        {
            ActorTorchTimer? timer = _body.TorchTimer;
            if (timer == null) return;

            bool wants = (flags & EnemySnapshotMessage.FlagTorch) != 0;
            if (wants == timer.IsUsingTorch) return;

            GlobalAssetsGameHandler? assets = GlobalAssetsGameHandler.TryGet();
            Il2CppDrova.Items.Item? torch = assets == null ? null : assets.GetTorchItem();
            if (torch == null) return;

            if (wants)
            {
                _body.GetEquipmentModule().EquipOnFirstEmptySlot(torch, true);
            }
            else
            {
                _body.GetEquipmentModule().TryUnequipInActiveSet(torch);
            }
        }

        /// <summary>
        /// Mirrors the host's knockout through the game's <c>BrawlActor</c>, since a client's brawl ends on seeing it here.
        /// </summary>
        private void ApplyKnockout(byte flags)
        {
            bool wanted = (flags & EnemySnapshotMessage.FlagKnockout) != 0;
            if (wanted == (_knockout != null)) return;

            if (wanted)
            {
                // Deaths travel on their own message.
                if (IsDead()) return;

                Il2CppDrova.Brawl.BrawlActor brawler = new(_body);
                brawler.Knockout();
                _knockout = brawler;

                MelonLogger.Msg("[Enemy] a driven creature went down where its brain lives; " +
                    "its body here was laid out to match.");
                return;
            }

            StandUp();
        }

        /// <summary>Stands a mirrored knockout back up, clearing <c>KeepKnockoutState</c> first so the game allows it.</summary>
        private void StandUp()
        {
            if (_knockout == null) return;

            try
            {
                if (_body.CustomStats != null)
                {
                    _body.CustomStats.KeepKnockoutState = false;
                }

                _knockout.RemoveKnockout();

                MelonLogger.Msg("[Enemy] a driven creature stood back up where its brain lives; " +
                    "its body here got up to match.");
            }
            finally
            {
                _knockout = null;
            }
        }

        /// <summary>
        /// Starts the swing the host says this creature is making, once per wrapping sequence number.
        /// By prefab first, since a driven creature's AI never installed the binds of its natural weapon.
        /// </summary>
        private void ApplyAction(in EnemySnapshotMessage.Record record)
        {
            bool hasBind = record.Action != EnemySnapshotMessage.NoAction;

            if (!hasBind && record.ActionHash == 0)
            {
                SettleIdle();
                return;
            }

            _idleSince = -1f;

            if (_hasAppliedAction && !PlayerStateMessage.IsNewerAction(record.ActionSequence, _appliedActionSequence))
            {
                KeepPlaying();
                return;
            }

            _lostSince = -1f;

            Actor.ActionModule? actions = _body.GetActionModule();
            if (actions == null) return;

            _appliedActionSequence = record.ActionSequence;
            _hasAppliedAction = true;

            // Requested, not forced, so a stunned or dead body refuses it.
            if (ActionPrefabIndex.TryResolve(record.ActionHash, out Il2CppDrova.ActorActions.AA_ABase? prefab))
            {
                // Death wipes the instance map, so this recreates the swing after a revive.
                actions.GetOrCreateActionInstanceFromPrefab(prefab, null, null);
                actions.TryStartAction(prefab);

                _heldPrefab = prefab;
                _heldAction = Actor.ActionModule.EActionBind.None;
                _releaseApplied = false;
                return;
            }

            if (!hasBind) return;

            Actor.ActionModule.EActionBind bind = (Actor.ActionModule.EActionBind)record.Action;

            actions.TryStartAction(bind);

            _heldAction = bind;
            _heldPrefab = null;
            _releaseApplied = false;
        }

        /// <summary>
        /// Ends an action this copy still plays after the host's stopped, since a cancel never crosses.
        /// Waited out first, so an ordinary blow still plays to its end.
        /// </summary>
        private void SettleIdle()
        {
            Actor.ActionModule? actions = _body.GetActionModule();
            if (actions == null || !actions.HasCurrentAction)
            {
                _idleSince = -1f;
                return;
            }

            if (_body.CustomStats != null && _body.CustomStats.IsKnockout) return;
            if (IsDead()) return;

            if (_idleSince < 0f)
            {
                _idleSince = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _idleSince < DisagreeSeconds) return;

            actions.ClearCurrentAndQueuedActions();
            IdleCleared++;
            _heldPrefab = null;
            _heldAction = Actor.ActionModule.EActionBind.None;
            _idleSince = -1f;
        }

        /// <summary>
        /// Restarts an action the host still plays and this copy has stopped.
        /// Waited out first, so a blow finished a moment early is not swung twice.
        /// </summary>
        private void KeepPlaying()
        {
            Actor.ActionModule? actions = _body.GetActionModule();
            if (actions == null || _heldPrefab == null || actions.HasCurrentAction || _releaseApplied)
            {
                _lostSince = -1f;
                return;
            }

            if (_body.CustomStats != null && _body.CustomStats.IsKnockout) return;
            if (IsDead()) return;

            if (_lostSince < 0f)
            {
                _lostSince = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime - _lostSince < DisagreeSeconds) return;

            actions.GetOrCreateActionInstanceFromPrefab(_heldPrefab, null, null);
            actions.TryStartAction(_heldPrefab);
            _lostSince = -1f;
            KeptPlaying++;
        }

        /// <summary>
        /// Releases the held swing when the host has, which turns a wind-up into a blow.
        /// Read from every update's flag, so a lost packet costs nothing.
        /// </summary>
        private void ApplyRelease(in EnemySnapshotMessage.Record record)
        {
            if (_releaseApplied) return;
            if (_heldPrefab == null && _heldAction == Actor.ActionModule.EActionBind.None) return;
            if ((record.Flags & EnemySnapshotMessage.FlagActionReleased) == 0) return;

            Actor.ActionModule? actions = _body.GetActionModule();
            if (actions == null) return;

            if (_heldPrefab != null)
            {
                actions.TryReleaseAction(_heldPrefab);
            }
            else
            {
                actions.TryReleaseAction(_heldAction);
            }

            _releaseApplied = true;
        }

        private bool IsDead()
        {
            Health? health = _body.GetHealth();
            return health != null && health.IsDead;
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
