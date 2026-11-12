using Drova_Modding_API.Access;
using Drova_Modding_API.Extensions;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
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
        /// Pull rate onto the reported position, applied as <c>1 - e^(-rate * dt)</c>.
        /// Faster than the update gap, slow enough not to reproduce link jitter.
        /// </summary>
        private const float CorrectionRate = 30f;

        /// <summary>The error past which the body is placed instead of slid there.</summary>
        private const float SnapDistance = 120f;

        /// <summary>Below this the error is dropped, since the wire quantizes position to a quarter unit.</summary>
        private const float SettledDistance = 0.25f;

        /// <summary>How long the host's creature must have stood on one spot before the copy is put on it outright.</summary>
        private const float StillSeconds = 0.1f;

        /// <summary>Fastest a creature is assumed to move when leading its copy. Above a charge, below a glitch.</summary>
        private const float MaxPredictedSpeed = 400f;

        /// <summary>Furthest ahead a copy is led, so a creature that stopped is not carried far past where it stood.</summary>
        private const float MaxLeadSeconds = 0.12f;

        /// <summary>How far off its spot a standing copy counts as shoved.</summary>
        private const float ShovedDistance = 8f;

        /// <summary>How long the host and this copy must disagree about an action before this copy is corrected.</summary>
        private const float DisagreeSeconds = 1.5f;

        /// <summary>How long a host swing waits for this copy to finish winding up the one before, before that one is cut.</summary>
        private const float PendingSeconds = 0.5f;

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
        private byte _seenSequence;
        private bool _hasSeenSequence;

        /// <summary>The host's next swing, held back while this copy still winds up the last one.</summary>
        private Il2CppDrova.ActorActions.AA_ABase? _pendingPrefab;
        private float _pendingSince;
        private bool _pendingReleased;

        /// <summary>When the disagreement <see cref="SettleIdle"/> waits out began.</summary>
        private float _idleSince = -1f;

        /// <summary>When the disagreement <see cref="KeepPlaying"/> waits out began.</summary>
        private float _lostSince = -1f;

        private bool _hasAppliedStance;
        private bool _appliedInCombat;
        private bool _appliedStun;
        private Il2CppDrova.Brawl.BrawlActor? _knockout;
        private Vector2 _error;
        private bool _hasError;

        /// <summary>Where the host last had this creature, held to while the host says it stands.</summary>
        private Vector2 _anchor;
        private float _anchorSince;

        /// <summary>The body's own force setting before take-over, put back on hand-back.</summary>
        private bool? _receivedForce;

        /// <summary>Whether this class switched the body's own physics off, so only its own switch is undone.</summary>
        private bool _isHeld;

        /// <summary>When the last update arrived, and how fast the host's creature moved between the last two, for dead reckoning.</summary>
        private float _anchorAt;
        private Vector2 _anchorVelocity;
        private bool _isAnchored;

        /// <summary>How many long actions were started again because the host was still playing them. For tests.</summary>
        internal static int KeptPlaying { get; private set; }

        /// <summary>Host stuns mirrored onto driven bodies here. For tests.</summary>
        internal static int Stunned { get; private set; }

        /// <summary>How many leftover actions were ended because the host had stopped. For tests.</summary>
        internal static int IdleCleared { get; private set; }

        /// <summary>Late-playing swings cut short so the host's next one could start. For tests.</summary>
        internal static int CutShort { get; private set; }

        /// <summary>Times a standing copy was found shoved well off the host's spot. For tests.</summary>
        internal static int Shoved { get; private set; }

        /// <summary>Updates whose gap was too big to ease, so the copy jumped onto the host's spot. For tests.</summary>
        internal static int Snaps { get; private set; }

        /// <summary>Host swings this copy still refused after its late one was cut short. For tests.</summary>
        internal static int SwingsDropped { get; private set; }

        /// <summary>Host swings and combo steps this machine's copies were asked to start. For tests.</summary>
        internal static int SwingsReplayed { get; private set; }

        /// <summary>Changes of action on the host that no record brought to this machine. For tests.</summary>
        internal static int TransitionsMissed { get; private set; }

        /// <summary>Target questions about driven bodies answered with the player the host's AI fights. For tests.</summary>
        internal static int AbilitiesAimed { get; private set; }

        /// <summary>Each driven body's target as the host last reported it, by native address.</summary>
        private static readonly Dictionary<IntPtr, PlayerId> Targets = new();

        /// <summary>The player the machine driving this body said it fights.</summary>
        internal static PlayerId ReportedTarget(Actor body) => Targets.TryGetValue(body.Pointer, out PlayerId player) ? player : PlayerId.None;

        /// <summary>Whether the wire steers this body with its own AI off.</summary>
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
        /// Forgets which swing was last applied, so the next record is a baseline. Used for a sleeping body,
        /// whose wrapped swing counter would otherwise read every later swing as old.
        /// </summary>
        internal void ForgetSwing()
        {
            _hasAppliedAction = false;
            _heldPrefab = null;
            _heldAction = Actor.ActionModule.EActionBind.None;
            _releaseApplied = false;
            _pendingPrefab = null;
            _hasSeenSequence = false;
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
            // Before the take-over check: a refused take-over still recorded a target.
            if (_body != null) Targets.Remove(_body.Pointer);

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
                _pendingPrefab = null;
                _error = Vector2.zero;
                _hasError = false;
                _isAnchored = false;
                HoldStill(false);

                if (_receivedForce is bool received) _body.GetEntityRigidbody()?.SetReceivesForce(received);
                _receivedForce = null;
                _hasAppliedStance = false;
                _appliedStun = false;
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

                    // A standing creature is re-sent only on keepalive and the local player shoves this AI-less body,
                    // so it is held to the spot. A charge moves the host's creature without a walk direction, so its
                    // speed is read off the updates for Tick to lead the copy.
                    float now = Time.unscaledTime;
                    float since = now - _anchorAt;
                    // Two batches can share a frame. The later one then has no time to measure a speed over.
                    if (since > 0f)
                    {
                        _anchorVelocity = position == _anchor || since > StaleSeconds
                            ? Vector2.zero
                            : Vector2.Lerp(_anchorVelocity, Vector2.ClampMagnitude((position - _anchor) / since, MaxPredictedSpeed), 0.5f);
                    }

                    _anchorAt = now;

                    if (position != _anchor) _anchorSince = now;
                    _anchor = position;
                    _isAnchored = !isMoving;
                    HoldStill(_isAnchored);

                    movement.SetDirection(isMoving ? Angle(record.MoveAngle) : Vector2.zero);
                    movement.SetIntendedMode(ToMoveMode((byte)((record.Flags >> 1) & 0b11)));
                }

                Actor.LookModule? look = _body.GetLookModule();
                if (look != null)
                {
                    look.SetLookDirection(Angle(record.LookAngle));
                }

                Targets[_body.Pointer] = record.Target;

                ApplyStance(record.ModeAndTarget);
                ApplyCombatState(record.Flags);
                ApplyKnockout(record.Flags);
                ApplyStun(record.ModeAndTarget);
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

            RefuseForces();
            StartPending();

            if (_isAnchored)
            {
                // A moving creature is sent every combat tick. Past a few without one it stopped, and the next keepalive
                // would otherwise find the copy carried on past the spot.
                float age = Time.unscaledTime - _anchorAt;
                float lead = age > MaxLeadSeconds ? 0f : Mathf.Min(age + OneWaySeconds(), MaxLeadSeconds);
                _error = _anchor + (_anchorVelocity * lead) - _body.GetFeetPosition();

                // Easing closes any lag well within a fifth of a second, so a gap that old came from a push.
                if (Time.unscaledTime - _anchorSince > 0.2f && _error.sqrMagnitude > ShovedDistance * ShovedDistance) Shoved++;

                _hasError = _error.sqrMagnitude > SettledDistance * SettledDistance && _error.sqrMagnitude <= SnapDistance * SnapDistance;

                // Standing still on the host: put exactly there, so nothing this copy plays on its own moves it.
                if (_hasError && Time.unscaledTime - _anchorSince > StillSeconds)
                {
                    Actor.MovementModule? movement = _body.GetMovementModule();
                    if (movement != null) Place(movement, _anchor);
                    _error = Vector2.zero;
                    _hasError = false;
                }
            }

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
        /// The first is set instantly, since a mode transition blocks movement for about a second.
        /// Later ones compare against <c>QueuedMode</c>, which <c>RequestMode</c> writes.
        /// </summary>
        private void ApplyStance(byte modeAndTarget)
        {
            byte value = (byte)(modeAndTarget & 0x0F);
            if (value == EnemySnapshotMessage.NoValue) return;

            Actor.EInteractionMode wanted = (Actor.EInteractionMode)value;

            if (!Enum.IsDefined(wanted)) return;

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
        /// Switches a standing copy's own physics off. The host stands its creature on the teammate's walk-through body,
        /// so here it overlaps the solid local player and gets pushed off the spot. The collider stays.
        /// </summary>
        private void HoldStill(bool held)
        {
            if (held == _isHeld) return;

            EntityRigidbody? physics = _body.GetEntityRigidbody();
            if (physics == null) return;

            if (held)
            {
                if (!physics.IsSimulated) return;
                physics.DeactivatePhysics();
            }
            else
            {
                physics.ActivatePhysics();
            }

            _isHeld = held;
        }

        /// <summary>
        /// Keeps the copy's own moves from pushing it, since the host's movement already arrives in its updates.
        /// Every frame, because the game can switch forces back on and a jump pushes through a force source the switch misses.
        /// </summary>
        private void RefuseForces()
        {
            EntityRigidbody? physics = _body.GetEntityRigidbody();
            if (physics == null) return;

            if (physics._forceSources != null && physics._forceSources.Count > 0) physics._forceSources.Clear();

            if (!physics.ReceivesForce) return;

            _receivedForce ??= true;
            physics.SetReceivesForce(false);
            physics.ResetForce();
        }

        /// <summary>Half the round trip to the host, the age of every update on arrival. Zero on the host, which drives leased copies.</summary>
        private static float OneWaySeconds()
        {
            return CoopSession.State == CoopSessionState.Joined && CoopSession.RoundTripMs(PlayerId.Host) is float roundTrip
                ? roundTrip / 2000f
                : 0f;
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
                Snaps++;

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

        /// <summary>
        /// Stuns the body when the host's poise broke by breaking its own, so the game plays the same stagger and ends it.
        /// Its poise never drops here on its own, since its blows are decided on the host.
        /// </summary>
        private void ApplyStun(byte modeAndTarget)
        {
            bool wanted = (modeAndTarget & EnemySnapshotMessage.PoiseBroken) != 0;
            if (wanted == _appliedStun) return;

            _appliedStun = wanted;
            if (!wanted || IsDead()) return;

            if (PuppetPoise.Break(_body.PoiseController)) Stunned++;
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

            // The host counts every change of action, so a gap is a change no record brought here.
            byte step = unchecked((byte)(record.ActionSequence - _seenSequence));
            if (_hasSeenSequence && step is > 1 and <= 127) TransitionsMissed += step - 1;
            if (!_hasSeenSequence || step <= 127) _seenSequence = record.ActionSequence;
            _hasSeenSequence = true;

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

            SwingsReplayed++;

            _appliedActionSequence = record.ActionSequence;
            _hasAppliedAction = true;

            // Requested, not forced, so a stunned or dead body refuses it.
            if (ActionPrefabIndex.TryResolve(record.ActionHash, out Il2CppDrova.ActorActions.AA_ABase? prefab))
            {
                // The host is two swings on while this copy still winds up, so the one in between is never shown.
                if (_pendingPrefab != null) SwingsDropped++;

                _pendingPrefab = null;

                if (!Start(actions, prefab, cut: false))
                {
                    _pendingPrefab = prefab;
                    _pendingSince = Time.unscaledTime;
                    _pendingReleased = false;
                }

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
        /// Starts a host swing on this copy. Creature attacks cannot interrupt or queue during a wind-up, and this copy plays
        /// the host's last one late. Cutting it there would hide the blow, so unless <paramref name="cut"/> the new swing is refused
        /// while the old one still winds up. A combo step restarts its chain's root, the only name that crosses.
        /// </summary>
        private bool Start(Actor.ActionModule actions, Il2CppDrova.ActorActions.AA_ABase prefab, bool cut)
        {
            // Death wipes the instance map, so this recreates the swing after a revive.
            actions.GetOrCreateActionInstanceFromPrefab(prefab, null, null);
            if (actions.TryStartAction(prefab) == Actor.ActionModule.EStartResult.OtherActionInProgress)
            {
                if (!cut && IsWindingUp(actions)) return false;

                actions.ForceEndCurrentAction(Il2CppDrova.ActorActions.AA_ABase.EEndMode.Interrupt);
                actions.GetOrCreateActionInstanceFromPrefab(prefab, null, null);
                if (actions.TryStartAction(prefab) is Actor.ActionModule.EStartResult.ActionStarted or Actor.ActionModule.EStartResult.ActionQueued) CutShort++;
                else SwingsDropped++;
            }

            _heldPrefab = prefab;
            _heldAction = Actor.ActionModule.EActionBind.None;
            _releaseApplied = false;
            return true;
        }

        /// <summary>Starts the held-back swing once the one before it has struck, or has kept it waiting too long.</summary>
        private void StartPending()
        {
            if (_pendingPrefab == null) return;

            Actor.ActionModule? actions = _body.GetActionModule();
            if (actions == null) return;

            // Asked by phase, since every start request can spend a combo chain's single chance.
            if (IsWindingUp(actions) && Time.unscaledTime - _pendingSince < PendingSeconds) return;

            Il2CppDrova.ActorActions.AA_ABase prefab = _pendingPrefab;
            _pendingPrefab = null;

            try
            {
                Start(actions, prefab, cut: true);

                if (_pendingReleased)
                {
                    actions.TryReleaseAction(prefab);
                    _releaseApplied = true;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] starting a creature's held-back swing failed: " + e);
            }
        }

        private static bool IsWindingUp(Actor.ActionModule actions)
        {
            Il2CppDrova.ActorActions.AA_AttackAction? attack = actions.HasCurrentAction
                ? actions.CurrentAction.PooledCast<Il2CppDrova.ActorActions.AA_AttackAction>()
                : null;

            return attack != null && attack.CurrentPhase is Il2CppDrova.ActorActions.AA_AttackAction.Phase.None
                or Il2CppDrova.ActorActions.AA_AttackAction.Phase.Antp or Il2CppDrova.ActorActions.AA_AttackAction.Phase.Hold;
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
            // The flag speaks of the host's current swing, which here has not begun.
            if (_pendingPrefab != null)
            {
                _pendingReleased = (record.Flags & EnemySnapshotMessage.FlagActionReleased) != 0;
                return;
            }

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

        /// <summary>
        /// Answers a driven body's target question with the player the host's AI fights. Its own AI is off, so a frog's mortar,
        /// a leap or a spawn at the target's feet otherwise lands on the creature itself.
        /// </summary>
        internal static void AimAtHostTarget(Actor? owner, ref Il2CppDrova.AI.Logic.IAIActor? target)
        {
            if (Targets.Count == 0 || owner == null) return;

            try
            {
                if (!Targets.TryGetValue(owner.Pointer, out PlayerId player) || !player.IsAssigned) return;

                Actor? body = null;
                if (player == CoopSession.LocalPlayerId) PlayerAccess.TryGetPlayer(out body);
                else if (RemotePlayers.Bodies.TryGetValue(player, out RemotePlayer? remote)) body = remote.Body;

                Il2CppDrova.AI.Logic.IAIActor? aimed = body?.AIModule?.GetIAIFactory()?.GetActor();
                if (aimed == null) return;

                target = aimed;
                AbilitiesAimed++;
            }
            catch (Exception e)
            {
                // Swallowed: a throw would lose the ability's whole placement.
                MelonLogger.Error("[Enemy] aiming a driven creature's ability failed: " + e);
            }
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
