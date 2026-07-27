using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Items;
using Il2CppDrova.Sfx.Voices;
using Il2CppDrova.StaminaSystem;
using Il2CppOpenWorldSystem;
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
        ///
        /// Measured against what ordinary running produces, not guessed. A remote player at a run reaches
        /// a drift of about 42 units - the easing is closing a gap that latency keeps reopening - and the
        /// original 50 sat close enough to that to fire on a straight sprint: three relocations inside two
        /// seconds in one test, each one a visible jump for something that was merely behind. The gap this
        /// exists to catch is a teleport or a scene change, which is hundreds of units, so there is a great
        /// deal of room between the two and no reason to sit near the wrong end of it.
        /// </summary>
        private const float TeleportDistance = 150f;

        private PlayerStateMessage _state;
        private bool _hasState;

        // Which action this body was last told to start, and the sequence that told it. Kept because a
        // release names the action being released, and by the time the owner has stopped, the wire is
        // already saying they are doing nothing.
        /// <summary>
        /// How long a request to start an action keeps trying before it is given up on. Long enough to
        /// cover the link and a wind-up phase, short enough that it cannot land on a later swing the
        /// player never made.
        /// </summary>
        private const float PendingActionSeconds = 0.4f;

        private Actor.ActionModule.EActionBind _activeAction = Actor.ActionModule.EActionBind.None;
        private Actor.ActionModule.EActionBind _pendingAction = Actor.ActionModule.EActionBind.None;
        private float _pendingSeconds;
        private bool _releaseApplied;
        private byte _appliedActionSequence;
        private bool _hasAppliedAction;

        // Which equipment message this body is already wearing. Zero is "nothing has arrived", and the
        // session counts from one, so a body dresses itself the first time it hears anything.
        private int _appliedEquipmentVersion;

#if DEBUG
        private float _sinceDriftReport;

        // Set when a remote action starts, counted down, then reported. A swing costs stamina over its
        // length rather than at its start, so the numbers at the moment it begins say nothing.
        private const float StaminaReportDelay = 1f;
        private float _staminaReportIn = -1f;
        private float _localStaminaBefore;
        private float _puppetStaminaBefore;
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
        /// Puts on what its owner is wearing, if that has changed since the last time.
        ///
        /// **This decides which attack the body performs**, not only what it looks like. Equipping a
        /// weapon is what binds that weapon's action to <c>PrimaryAttack</c>, so a body with empty hands
        /// has the bare-handed action bound and plays it however faithfully the attack was replicated.
        ///
        /// Everything is taken off first. Applying a difference would need the two sides to agree about
        /// which slot each item sits in, and they do not have to: the item knows what it can be equipped
        /// as, so handing each one to the module and letting it choose is both shorter and immune to two
        /// machines laying their slots out differently.
        ///
        /// Nothing here charges the far player twice. <c>ItemBhvr_EquipEffect_StatModification</c> applies
        /// to <c>actor.GetStats()</c> - this body's own component - so the stat bonuses land on the puppet
        /// and stay there.
        /// </summary>
        internal void RecordEquipment(EquipmentSnapshot snapshot)
        {
            if (snapshot.Version == _appliedEquipmentVersion) return;
            if (Body == null) return;

            try
            {
                Actor.EquipmentModule equipment = Body.GetEquipmentModule();
                if (equipment == null) return;

                // The game refuses to equip anything on a player who is mid-action, and this body counts
                // as a player for that test: Actor.Awake sets IsPlayer from the EntityInfo asset, which a
                // clone shares with the real player, and ActorEquipSlot.TryEquipIntern reads exactly that.
                // A remote player is mid-swing a great deal of the time, so applying anyway would fail
                // silently and leave the body in whatever it happened to be wearing.
                //
                // Deferred rather than forced. The version is left unconsumed, and this runs every frame,
                // so the change lands as soon as the swing ends - which is also when it stops being able
                // to interrupt an attack that is already replicating.
                Actor.ActionModule actions = Body.GetActionModule();
                if (actions != null && actions.HasCurrentAction) return;

                _appliedEquipmentVersion = snapshot.Version;

                // The build first, and before a single item goes on. The skin is applied by the skin
                // item's own equip effect, and all that effect does is set a skin id on whatever build the
                // actor already has - so putting the items on first paints the owner's skin onto the
                // prefab's body, which is what made the hair, beard and scars sit wrong.
                ApplyBuild(snapshot);

                SubDatabase_Item? database = ProviderAccess.ItemDatabase;
                if (database == null)
                {
                    MelonLogger.Warning("[Equip] no item database yet; the body stays as it is.");
                    return;
                }

                equipment.UnequipAll();

                int dressed = 0;
                foreach (string id in snapshot.ItemIds)
                {
                    Item item = database.GetItemByReadableId(id);
                    if (item == null)
                    {
                        // A real possibility rather than a defensive check: the other player may be running
                        // a mod that adds items this game does not have. Naming it is the whole value of
                        // the line, because the symptom otherwise is a body missing one piece of gear.
                        MelonLogger.Warning($"[Equip] {Id} has '{id}', which this game does not know. Mod sets may differ.");
                        continue;
                    }

                    // True is replaceLast: when no slot of the item's type is free, put it in the last one
                    // rather than giving up. On a body being dressed to match somebody else there is no
                    // such thing as a slot worth protecting - whatever is in the way is a leftover of the
                    // gear this body is being taken out of.
                    if (equipment.EquipOnFirstEmptySlotInActiveSet(item, true))
                    {
                        dressed++;
                        continue;
                    }

                    // Named, not counted. A total told us that some things did not fit and left which ones
                    // to guesswork; the slot type is what says whether a whole category is being refused
                    // or one particular item is.
                    item.TryGetItemBehaviour<ItemBhvr_IEquippable>(out ItemBhvr_IEquippable? equippable);
                    string slot = equippable?.GetEquipmentSlotType()?.name ?? "no equippable behaviour";

                    MelonLogger.Warning($"[Equip] {Id}: '{id}' was refused (slot type: {slot}).");
                }

                MelonLogger.Msg($"[Equip] {Id} is now wearing {dressed} of {snapshot.ItemIds.Length} items: " +
                    string.Join(", ", snapshot.ItemIds));
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Equip] dressing {Id} failed: " + e);
            }
        }

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
        /// What the streaming system is doing where this body stands, in one line for
        /// <c>coop_status</c>.
        ///
        /// This is the only way to tell the difference between a remote player standing in a world and a
        /// remote player standing in nothing. Both look the same from here - the body is drawn either way,
        /// because the body is not part of the chunk that is missing - so whether the ground under them
        /// exists has to be asked rather than observed.
        /// </summary>
        internal string DescribeStreaming()
        {
            if (Body == null) return "no body";

            try
            {
                OW_Root root = OW_Root.CurrentRoot;
                if (root == null) return "no streaming root in this scene";

                Vector3 here = Body.transform.position;
                OW_RuntimeChunkInfo chunk = root.GetRuntimeChunkInfoAtWorldPos(new Vector2(here.x, here.y));

                if (chunk == null) return "standing outside every chunk";

                // The essential count is what proves the puppet's own reference point registered. A chunk
                // can perfectly well read as loaded because the local player is standing in it too, which
                // is exactly the case where a missing point goes unnoticed.
                return $"chunk ({chunk.Id.x},{chunk.Id.y}) {chunk.State}, {chunk.EssentialRefPointCount} essential reference points";
            }
            catch (Exception e)
            {
                return "streaming state unavailable: " + e.Message;
            }
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

                // A body exists for a frame or two before its modules are built, and driving one in that
                // window throws out of IL2CPP rather than returning empty-handed: setting a move mode
                // reaches SetActorSpeed, which dereferences a speed object that does not exist yet.
                //
                // The speed object is the signal, not IsValid. IsValid only asks whether the module has an
                // actor, and it has one before it has anything else - it passed on every one of these
                // frames while the module was still half-built. The speed object is created in the same
                // pass as the rest of the module's insides, so having it means the rest is there too.
                if (movement == null || movement.SpeedModule == null) return;

#if DEBUG
                ReportDrift(movement, unscaledDeltaTime);
                ReportStamina(unscaledDeltaTime);
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

                ApplyStance();
                Follow(movement, unscaledDeltaTime);
                ApplyAction(unscaledDeltaTime);
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Puppet] applying intent to {Id} failed: " + e);
            }
        }

        /// <summary>
        /// Gives the body its owner's build and voice.
        ///
        /// A clone comes out of the player prefab with the prefab's build, and no amount of dressing
        /// changes that: the build is a component on the actor, not a slot. Setting it is the whole fix
        /// for a remote player who is the wrong shape, and - because the skin item only sets a skin id on
        /// the build that is already there - for their skin, hair and scars being wrong too.
        ///
        /// <c>None</c> is skipped rather than applied. It is what a sender that could not read its own
        /// build sends, and imposing it would strip a body of the build it has for no reason.
        /// </summary>
        private void ApplyBuild(EquipmentSnapshot snapshot)
        {
            try
            {
                ActorBodyType build = Body.GetActorGender();
                if (build == null) return;

                ActorBodyType.BodyType wanted = (ActorBodyType.BodyType)snapshot.BodyType;
                if (wanted != ActorBodyType.BodyType.None && build.GetBodyType() != wanted)
                {
                    build.SetGender(wanted);
                }

                VoiceID voice = (VoiceID)snapshot.VoiceId;
                if (voice != VoiceID.Unknown && build.GetVoiceId() != voice)
                {
                    build.SetVoiceId(voice);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[Equip] could not set {Id}'s build: " + e.Message);
            }
        }

        /// <summary>
        /// Puts the body into the stance its owner is in - weapon drawn or away, sitting, kneeling.
        ///
        /// **Equipping a weapon is not holding it.** A body that owns a spear but is in <c>Normal</c> has
        /// it stowed, and performs the spear's attack with its fists: the attack replicates perfectly and
        /// looks absurd, which is how this was found. The stance is what puts the weapon in its hands.
        ///
        /// Asked for rather than set, through the game's own <c>RequestMode</c>, so the change plays its
        /// transition - the actual draw animation - instead of the weapon appearing. Requested only on a
        /// difference, because the module is already working towards a target and repeating it every frame
        /// would restart that.
        /// </summary>
        private void ApplyStance()
        {
            if (!TryReadStance(_state.InteractionMode, out Actor.EInteractionMode wanted)) return;

            Actor.InteractionModeModule stance = Body.GetInteractionModule();
            if (stance == null) return;

            if (stance.TargetMode == wanted) return;

            stance.RequestMode(wanted);
        }

        /// <summary>
        /// Reads a stance off the wire, refusing anything this build does not know - the numbering is the
        /// game's, so a peer on another version can name a mode that does not exist here.
        /// </summary>
        private static bool TryReadStance(byte value, out Actor.EInteractionMode mode)
        {
            mode = (Actor.EInteractionMode)value;

            return mode is Actor.EInteractionMode.Normal
                or Actor.EInteractionMode.Combat
                or Actor.EInteractionMode.Sit
                or Actor.EInteractionMode.Kneel
                or Actor.EInteractionMode.KnockOut
                or Actor.EInteractionMode.Squat
                or Actor.EInteractionMode.Sleep
                or Actor.EInteractionMode.SitFloor
                or Actor.EInteractionMode.Smoke
                or Actor.EInteractionMode.Lyra
                or Actor.EInteractionMode.Cheer;
        }

        /// <summary>
        /// Starts and releases actions on the body as its owner starts and releases them.
        ///
        /// The action is handed to the game's own action module, which is what the local input layer does
        /// with it, so the swing, its timing, its animation and its recovery are the game's rather than an
        /// imitation of them. Nothing here says what an attack looks like, and nothing here should.
        ///
        /// No reentrancy guard, and none is needed. The guard exists for sync that is driven by hooks,
        /// where applying a change re-enters the very method that watches for it; this reads the local
        /// player by polling and writes only to bodies that are not the local player, so there is no path
        /// back to the sender.
        /// </summary>
        private void ApplyAction(float unscaledDeltaTime)
        {
            Actor.ActionModule actions = Body.GetActionModule();
            if (actions == null) return;

            bool isNew = !_hasAppliedAction
                || PlayerStateMessage.IsNewerAction(_state.ActionSequence, _appliedActionSequence);

            if (!isNew)
            {
                RetryPending(actions, unscaledDeltaTime);
                ApplyRelease(actions);
                return;
            }

            _appliedActionSequence = _state.ActionSequence;
            _hasAppliedAction = true;

            if (!ActionBinds.TryFromWire(_state.Action, out Actor.ActionModule.EActionBind wanted))
            {
                MelonLogger.Warning($"[Puppet] {Id} is doing action {_state.Action}, which this build does not know; ignored.");
                _pendingAction = Actor.ActionModule.EActionBind.None;
                return;
            }

            // Released only when the body is being told to do something else, and by name. A held attack
            // ends when the far player lets go, which reaches here as the action becoming None - so the
            // thing being released is the one remembered here, never the one on the wire.
            //
            // The same action arriving twice is the combo case and must not be released. A release is the
            // button coming up, and the game reads that as the end of the chain: releasing between the two
            // swings of a combo would cancel the very thing being replicated. Starting it again while it
            // is already running is what the local input layer does, and the module turns that into the
            // combo itself.
            if (_activeAction != wanted && _activeAction != Actor.ActionModule.EActionBind.None)
            {
                actions.TryReleaseAction(_activeAction);
                _activeAction = Actor.ActionModule.EActionBind.None;
            }

            if (wanted == Actor.ActionModule.EActionBind.None)
            {
                _pendingAction = Actor.ActionModule.EActionBind.None;
                return;
            }

            _pendingAction = wanted;
            _pendingSeconds = PendingActionSeconds;
            _releaseApplied = false;

            RetryPending(actions, unscaledDeltaTime);
            ApplyRelease(actions);
        }

        /// <summary>
        /// Lets go of the button when its owner did.
        ///
        /// Separate from the action ending, and that distinction is the whole point. A tap starts a swing
        /// and releases at once while the swing runs on by itself for half a second; a body that waits for
        /// the swing to finish before letting go has held the button throughout, which is how an ordinary
        /// attack arrives as a charged one. Charged attacks looked right for the same reason - holding was
        /// the only thing being replicated.
        ///
        /// Driven by the flag on every packet rather than by the moment it changed, so a lost packet costs
        /// nothing: the next one still says the button is up. Applied once per use, because the game's own
        /// release path ignores a repeat but there is no reason to make the call.
        /// </summary>
        private void ApplyRelease(Actor.ActionModule actions)
        {
            if (_releaseApplied) return;
            if (_state.ActionReleased == 0) return;
            if (_activeAction == Actor.ActionModule.EActionBind.None) return;

            actions.TryReleaseAction(_activeAction);
            _releaseApplied = true;
        }

        /// <summary>
        /// Tries to start the action the far player began, and keeps trying briefly if the body is not
        /// ready for it yet.
        ///
        /// **A combo has a window, and this body is behind.** A combo may only begin during the signal,
        /// key or linger phase of the swing before it - that is <c>AA_AttackAction.CanComboStartedNow</c> -
        /// and a remote body is running roughly a hundred milliseconds behind its owner. So a second swing
        /// that fell inside the window on the owner's machine can easily arrive here while this body is
        /// still winding up, where the game refuses it outright and the chain is silently lost. Retrying
        /// for a short while lets the request wait for the window instead of missing it by the length of
        /// the link.
        ///
        /// Bounded deliberately. Past the window the answer stops being "too early" and becomes "no", and
        /// a request that keeps trying indefinitely would eventually land on a swing the player never
        /// made. Retrying is only safe at all because the body is watched rather than the result code -
        /// see below for why the module reports a started combo as a refusal, and what retrying through
        /// one costs.
        /// </summary>
        private void RetryPending(Actor.ActionModule actions, float unscaledDeltaTime)
        {
            if (_pendingAction == Actor.ActionModule.EActionBind.None) return;

            _pendingSeconds -= unscaledDeltaTime;

            // Read before the call and again after, because the result code cannot be trusted to say
            // whether anything happened. Also what tells a combo from a fresh attack in the log.
            ActionBinds.Snapshot before = ActionBinds.Current(actions);

            // The parameter is tryToQueue, and the game's own default for it is true. Passing false said
            // "refuse if anything is running", which is exactly what a combo is: the second swing arrives
            // while the first is still going and has to queue behind it.
            Actor.ActionModule.EStartResult result = actions.TryStartAction(_pendingAction, true);

            // **A started combo reports itself as a refusal.** Inside the module, a swing that continues a
            // chain is taken by `TryAnotherAction` on the running action; that path skips the queuing
            // branch entirely and falls through to `OtherActionInProgress` - the same code returned when
            // the module genuinely refused because something else was running. Whitelisting result codes
            // therefore reads a successful combo as a failure, leaves the request pending, and calls again
            // every frame: each retry that lands in the combo window starts another swing, and the one
            // that finally answers `ActionQueued` adds one more on top. Two swings arriving as three is
            // what that looks like from the outside.
            //
            // So the body is asked instead of the return value. Nothing but this call can have changed the
            // running action between the two samples, which makes a change proof that the request landed
            // whatever it was called - and it stays true for any other result code that turns out to have
            // an effect, rather than only for the one that has been found so far.
            ActionBinds.Snapshot after = ActionBinds.Current(actions);

            bool accepted = result is Actor.ActionModule.EStartResult.ActionStarted
                or Actor.ActionModule.EStartResult.ActionQueued
                || !after.IsSameUse(before);

            if (accepted)
            {
#if DEBUG
                MelonLogger.Msg($"[Act] {Id} start {_pendingAction} seq {_state.ActionSequence}: {result} " +
                    $"(body {before.Bind}/u{before.Usage}/c{before.Combo} -> " +
                    $"{after.Bind}/u{after.Usage}/c{after.Combo})");

                NoteStamina();
#endif

                _activeAction = _pendingAction;
                _pendingAction = Actor.ActionModule.EActionBind.None;
                return;
            }

            if (_pendingSeconds > 0f) return;

            // Worth a line every time rather than once. The game says exactly why it refused -
            // ActionIsMissing, ModuleDisabled, ConditionNotMet - and on a body assembled by this mod
            // rather than by the game, that answer is the difference between a bug in the clone and a
            // legitimate refusal.
            MelonLogger.Warning($"[Puppet] {Id} could not start {_pendingAction} within " +
                $"{PendingActionSeconds:0.00}s: {result}.");

            _pendingAction = Actor.ActionModule.EActionBind.None;
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
        /// Notes both stamina bars when a remote action starts, so the follow-up can say which one moved.
        /// </summary>
        private void NoteStamina()
        {
            _localStaminaBefore = LocalStamina();
            _puppetStaminaBefore = PuppetStamina();
            _staminaReportIn = StaminaReportDelay;
        }

        /// <summary>
        /// Says who paid for the remote player's last action.
        ///
        /// Which of the two numbers moved is the whole question. The puppet's own falling is correct and
        /// costs nothing - it is a body nobody plays. The local player's falling is the bug, and it means
        /// the cost is reaching a stat resolved by identity rather than by component, which the clone
        /// shares with the real player. Both unchanged while the on-screen bar moved would mean the bar is
        /// bound to the wrong actor and no stamina is being spent at all.
        /// </summary>
        private void ReportStamina(float unscaledDeltaTime)
        {
            if (_staminaReportIn < 0f) return;

            _staminaReportIn -= unscaledDeltaTime;
            if (_staminaReportIn > 0f) return;

            _staminaReportIn = -1f;

            float local = LocalStamina();
            float puppet = PuppetStamina();

            MelonLogger.Msg($"[Puppet] {Id} acted: local stamina {_localStaminaBefore:0.0} -> {local:0.0} " +
                $"(delta {local - _localStaminaBefore:+0.0;-0.0;0}), body {_puppetStaminaBefore:0.0} -> {puppet:0.0}. " +
                "Only the body's may move.");
        }

        private static float LocalStamina()
        {
            return PlayerAccess.TryGetPlayer(out Actor player) && player != null
                ? Stamina(player)
                : float.NaN;
        }

        private float PuppetStamina()
        {
            return Body != null ? Stamina(Body) : float.NaN;
        }

        private static float Stamina(Actor actor)
        {
            StaminaController controller = actor.GetStaminaController();
            return controller != null ? controller.CurStamina : float.NaN;
        }

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
