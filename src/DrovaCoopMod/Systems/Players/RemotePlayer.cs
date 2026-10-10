using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.ActorActions;
using Il2CppDrova.Alignment;
using Il2CppDrova.Brawl;
using Il2CppDrova.Items;
using Il2CppDrova.Sfx.Voices;
using Il2CppDrova.StaminaSystem;
using Il2CppOpenWorldSystem;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// One remote player's body. Intent (direction, move mode, look) drives its animation through the game's own modules,
    /// and the wire drives its position, since a clone has no input controller to move it.
    /// </summary>
    internal sealed class RemotePlayer
    {
        /// <summary>Fraction of the remaining gap closed per second when following the owner's position.</summary>
        private const float FollowRate = 14f;

        /// <summary>
        /// Past this distance the body is relocated instantly. Well above the ~42 units of drift a run produces,
        /// well below a teleport or scene change.
        /// </summary>
        private const float TeleportDistance = 150f;

        /// <summary>How long a start request keeps retrying, short enough not to land on a later swing.</summary>
        private const float PendingActionSeconds = 0.4f;

        /// <summary>How long a release waits for its queued swing to begin.</summary>
        private const float ReleaseWaitSeconds = 1.5f;

        /// <summary>How long a combo step may wait for the combo window, which opens at the previous swing's signal phase.</summary>
        private const float PendingComboSeconds = 0.9f;

        /// <summary>How long a body may be too busy to dress before that is logged.</summary>
        private const float EquipmentBlockedWarningSeconds = 8f;

        /// <summary>How often this body is made passable again.</summary>
        private const float PassabilityRefreshSeconds = 2f;

        private PlayerStateMessage _state;

        private ushort _maxHealth;

        private ushort _maxStamina;

        // A puppet carries no talents, so it is told whether the perfect-block window is open to it.
        private bool _hasPerfectBlock;
        private bool _hasState;

        private Actor.ActionModule.EActionBind _activeAction = Actor.ActionModule.EActionBind.None;
        private Actor.ActionModule.EActionBind _pendingAction = Actor.ActionModule.EActionBind.None;
        private float _pendingSeconds;

        /// <summary>Whether the pending start is the next step of the running swing. See <see cref="RetryPending"/>.</summary>
        private bool _pendingIsCombo;

        private bool _releaseApplied;

        // Set when a start was accepted without anything beginning (a queued swing). A change from the snapshot is that swing starting.
        private bool _releaseWaitsForStart;
        private ActionBinds.Snapshot _releaseWaitFrom;
        private float _releaseWaitSeconds;
        private byte _appliedActionSequence;
        private bool _hasAppliedAction;

        // Zero is "nothing arrived". The session counts from one.
        private int _appliedEquipmentVersion;

        /// <summary>The version of the talents last put on this body.</summary>
        private int _appliedTalents;

        private float _equipmentBlockedSeconds;
        private int _warnedEquipmentBlocked = -1;

        // The held item is kept because the release names it, and the owner may have selected another by then.
        private int _appliedItemUseVersion;
        private byte _appliedItemUseSequence;
        private bool _hasAppliedItemUse;
        private Item? _itemInUse;

        // Negative is "never heard".
        private int _appliedHealth = -1;

        // Kept, because removing the decoration needs the same reference.
        private IAlignment? _hostility;

        /// <summary>The decoration on the local player that makes this body an enemy of its summons.</summary>
        private IAlignment? _localHostility;
        private bool _isPassable;
        private float _sincePassable;

        // Null when the body is on its feet.
        private BrawlActor? _knockedOut;

        // Native address of the block whose clock was already slid back by a round trip. See AlignBlockWindow.
        private IntPtr _alignedBlock;

#if DEBUG
        /// <summary>Gap to the owner's position worth a log line, between normal follow drift and <see cref="TeleportDistance"/>.</summary>
        private const float DriftReportDistance = 3f;

        private float _sinceDriftReport;

        // A swing costs stamina over its length, so it is reported a second after it starts.
        private const float StaminaReportDelay = 1f;
        private float _staminaReportIn = -1f;
        private float _localStaminaBefore;
        private float _puppetStaminaBefore;
#endif

        /// <summary>Creates a remote player around an already-built body.</summary>
        internal RemotePlayer(PlayerId id, Actor body)
        {
            Id = id;
            Body = body;
        }

        /// <summary>Who this is.</summary>
        internal PlayerId Id { get; }

        /// <summary>The body being shown for them.</summary>
        internal Actor Body { get; }

        /// <summary>Whether this player's own game says they are on the floor, from the state they send.</summary>
        internal bool IsDown => _state.Downed;

        /// <summary>Whether they are down from a would-be lethal blow, or dead. Their word, as above.</summary>
        internal bool IsOut => _state.Out;

        /// <summary>Whether they are in a scripted brawl, as they report it.</summary>
        internal bool IsInBrawl => _state.InBrawl;

        /// <summary>Whether they are dead: out, with no health left. A downed player keeps some.</summary>
        internal bool IsDead => _state.Out && _state.Health == 0;

        /// <summary>
        /// Gives this body its owner's learned talents, written straight into the learned set to skip learning's player-facing events.
        /// </summary>
        internal void RecordTalents(string[] guids, int version)
        {
            if (version == _appliedTalents) return;

            try
            {
                Il2CppDrova.Talent.TalentActorModule? talents = Body.TalentActorModule?.TryCast<Il2CppDrova.Talent.TalentActorModule>();
                if (talents == null || talents._learnedTalents == null) return;

                talents._learnedTalents.Clear();
                foreach (string guid in guids) talents._learnedTalents.Add(guid);

                _appliedTalents = version;
                MelonLogger.Msg($"[Puppet] {Id} now carries its owner's {guids.Length} learned talents.");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Puppet] giving {Id} its owner's talents failed: " + e);
            }
        }

        /// <summary>
        /// Puts on what its owner is wearing, if changed. Everything comes off first, and each item picks its own slot.
        /// Equipping binds the weapon's action, so this also decides which attack the body plays.
        /// </summary>
        internal void RecordEquipment(EquipmentSnapshot snapshot)
        {
            if (snapshot.Version == _appliedEquipmentVersion) return;
            if (Body == null) return;

            // Gear put on an uninitialized actor attaches to nothing. Left unconsumed until the body is ready.
            if (!Body.IsInitialized) return;

            try
            {
                Actor.EquipmentModule? equipment = Body.GetEquipmentModule();
                if (equipment == null) return;

                // A clone counts as a player (shared EntityInfo), and the game refuses to equip a player mid-action.
                // Deferred, since this runs every frame and the version stays unconsumed.
                Actor.ActionModule? actions =Body.GetActionModule();
                if (actions != null && actions.HasCurrentAction)
                {
                    // Logged once per outfit after a wait, so a body never idle long enough to dress says so.
                    _equipmentBlockedSeconds += Time.unscaledDeltaTime;

                    if (_equipmentBlockedSeconds > EquipmentBlockedWarningSeconds && _warnedEquipmentBlocked != snapshot.Version)
                    {
                        _warnedEquipmentBlocked = snapshot.Version;
                        MelonLogger.Warning($"[Equip] {Id} has been mid-action for {_equipmentBlockedSeconds:0}s, " +
                            "so their outfit is still waiting. The body is wearing what the prefab shipped with.");
                    }

                    return;
                }

                _equipmentBlockedSeconds = 0f;

                // The build first: the skin item only sets a skin id on the existing build.
                ApplyBuild(snapshot);

                SubDatabase_Item? database = ProviderAccess.ItemDatabase;
                if (database == null)
                {
                    // Left unconsumed, since equipment is sent only on change.
                    MelonLogger.Warning("[Equip] no item database yet; the body stays as it is for now.");
                    return;
                }

                _appliedEquipmentVersion = snapshot.Version;

                equipment.UnequipAll();

                // Refusals are usually about order: CheckRequiredSlot refuses arrows until the quiver is on.
                List<(Item Item, byte Set)> refused = new();

                int dressed = 0;
                for (int index = 0; index < snapshot.ItemIds.Length; index++)
                {
                    string id = snapshot.ItemIds[index];
                    Item? item = database.GetItemByReadableId(id);
                    if (item == null)
                    {
                        // The other player may run a mod whose items this game lacks.
                        MelonLogger.Warning($"[Equip] {Id} has '{id}', which this game does not know. Mod sets may differ.");
                        continue;
                    }

                    // Into the set its owner keeps it in. The ammo slot belongs to every set, so it arrives marked as shared.
                    byte set = index < snapshot.ItemSets.Length
                        ? snapshot.ItemSets[index]
                        : PlayerEquipmentMessage.SharedSet;

                    if (TryEquip(equipment, item, set))
                    {
                        dressed++;
                        continue;
                    }

                    refused.Add((item, set));
                }

                // Before the second pass, since CheckRequiredSlot only looks at the active set.
                equipment.TrySelectEquipmentSet(snapshot.ActiveSet);

                dressed += DressAgain(equipment, refused);

                foreach ((Item item, byte _) in refused)
                {
                    // Named, so the log says whether a whole slot category is refused or one item.
                    item.TryGetItemBehaviour(out ItemBhvr_IEquippable? equippable);
                    string slot = equippable?.GetEquipmentSlotType()?.name ?? "no equippable behavior";

                    MelonLogger.Warning($"[Equip] {Id}: '{item.ReadableId}' was refused (slot type: {slot}).");
                }

                MelonLogger.Msg($"[Equip] {Id} is now wearing {dressed} of {snapshot.ItemIds.Length} items " +
                    $"(set {snapshot.ActiveSet} in hand): " + string.Join(", ", snapshot.ItemIds));
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Equip] dressing {Id} failed: " + e);
            }
        }

        /// <summary>
        /// Puts one item on the body in its owner's set. replaceLast is true, since whatever is in the way is a leftover.
        /// </summary>
        private static bool TryEquip(Actor.EquipmentModule equipment, Item item, byte set)
        {
            return set == PlayerEquipmentMessage.SharedSet
                ? equipment.EquipOnFirstEmptySlotInActiveSet(item, true)
                : equipment.EquipOnFirstEmptySlotInSet(item, set, true);
        }

        /// <summary>
        /// Offers refused items again until a pass equips nothing. Refused ammo would otherwise show "you need a quiver"
        /// (TriggerNeedsQuiver), since a puppet passes CheckIsPlayer.
        /// </summary>
        /// <returns>How many of them went on.</returns>
        private static int DressAgain(Actor.EquipmentModule equipment, List<(Item Item, byte Set)> refused)
        {
            int dressed = 0;
            bool anyWentOn = true;

            while (anyWentOn && refused.Count > 0)
            {
                anyWentOn = false;

                for (int index = refused.Count - 1; index >= 0; index--)
                {
                    if (!TryEquip(equipment, refused[index].Item, refused[index].Set)) continue;

                    refused.RemoveAt(index);
                    dressed++;
                    anyWentOn = true;
                }
            }

            return dressed;
        }

        /// <summary>
        /// Casts what its owner cast, if not yet performed. <c>CanBeUsedBy</c> is skipped on purpose, since it has side effects
        /// and the caster's game already allowed the cast. The spell deals no damage here, see <see cref="ItemUseWatch"/>.
        /// </summary>
        internal void RecordItemUse(ItemUseSnapshot snapshot)
        {
            if (snapshot.Version == _appliedItemUseVersion) return;
            if (Body == null) return;

            try
            {
                // The start and release often arrive collapsed into one poll, so a new sequence performs the use, then ends it.
                bool isNewUse = !_hasAppliedItemUse || snapshot.Sequence != _appliedItemUseSequence;

                if (isNewUse && !TryStartItemUse(snapshot)) return;

                _appliedItemUseVersion = snapshot.Version;
                _appliedItemUseSequence = snapshot.Sequence;
                _hasAppliedItemUse = true;

                if (snapshot.Released)
                {
                    ReleaseItemUse();
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Use] using '{snapshot.ItemId}' for {Id} failed: " + e);
            }
        }

        /// <summary>Puts the item in the body's hands and starts using it.</summary>
        /// <returns>False when the game is not ready yet. Nothing is consumed and it is retried next frame.</returns>
        private bool TryStartItemUse(ItemUseSnapshot snapshot)
        {
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;

            // Left unconsumed, since the database often lags a rebuilt world by a frame or two.
            if (database == null) return false;

            Item? item = database.GetItemByReadableId(snapshot.ItemId);
            if (item == null)
            {
                // The other player may run a mod whose abilities this game lacks.
                MelonLogger.Warning($"[Use] {Id} used '{snapshot.ItemId}', which this game does not know. Mod sets may differ.");

                // Consumed, since an unknown id will never appear.
                return true;
            }

            // A use whose release was lost. Left holding, the next one would never start on some abilities.
            ReleaseItemUse();

            _itemInUse = item;

            // Aimed abilities prefer the custom aim direction, which only the mouse writes on a real player.
            // Zeroed, so the game falls back to the replicated facing.
            Actor.LookModule? look = Body.GetLookModule();
            if (look != null)
            {
                look.SetCustomAbilityDir(Vector2.zero);
            }

            // Guards the local item-use postfix and names this cast, so anything it conjures frames later is attributed
            // to this player. See SummonWatch.
            SummonWatch.BeginCast(Id, snapshot.Sequence, Body);

            using (RemoteApply<ItemUseScope>.Enter())
            {
                item.StartUseItem(Body);
            }

            return true;
        }

        /// <summary>
        /// Takes the count of a cast without performing it, for a body built into a session already in progress.
        /// </summary>
        internal void AdoptItemUse(ItemUseSnapshot snapshot)
        {
            _appliedItemUseVersion = snapshot.Version;
            _appliedItemUseSequence = snapshot.Sequence;
            _hasAppliedItemUse = true;
        }

        /// <summary>Lets go of whatever this body is holding, if anything.</summary>
        private void ReleaseItemUse()
        {
            if (_itemInUse == null || Body == null) return;

            Item item = _itemInUse;
            _itemInUse = null;

            using (RemoteApply<ItemUseScope>.Enter())
            {
                item.EndUseItem(Body);
            }
        }

        /// <summary>Takes the newest state. Latest wins, <see cref="Follow"/> does the smoothing.</summary>
        internal void Record(PlayerStateMessage state)
        {
            _state = state;
            _hasState = true;
        }

        /// <summary>Takes the far player's bar maximums. Arrives on change, so it outlives any number of states.</summary>
        internal void RecordVitals(PlayerVitalsMessage vitals)
        {
            _maxHealth = vitals.MaxHealth;
            _maxStamina = vitals.MaxStamina;
            _hasPerfectBlock = vitals.HasPerfectBlock;
        }

        private static Vector2 Direction(byte packed)
        {
            PlayerStateMessage.UnpackAngle(packed, out float x, out float y);
            return new Vector2(x, y);
        }

        /// <summary>
        /// What streaming is doing where this body stands, for <c>coop_status</c>. The only way to tell whether the ground under it exists.
        /// </summary>
        internal string DescribeStreaming()
        {
            if (Body == null) return "no body";

            try
            {
                OW_Root? root = OW_Root.CurrentRoot;
                if (root == null) return "no streaming root in this scene";

                Vector3 here = Body.transform.position;
                OW_RuntimeChunkInfo? chunk =root.GetRuntimeChunkInfoAtWorldPos(new Vector2(here.x, here.y));

                if (chunk == null) return "standing outside every chunk";

                // The essential count proves the puppet's own reference point registered, even where the local player keeps the chunk loaded.
                return $"chunk ({chunk.Id.x},{chunk.Id.y}) {chunk.State}, {chunk.EssentialRefPointCount} essential reference points";
            }
            catch (Exception e)
            {
                return "streaming state unavailable: " + e.Message;
            }
        }

        /// <summary>Points the body the way its owner is facing and carries it to where its owner is.</summary>
        internal void Apply(float unscaledDeltaTime)
        {
            if (!_hasState || Body == null) return;

            try
            {
                Actor.MovementModule? movement = Body.GetMovementModule();

                // Modules are built a frame or two after the body. SpeedModule (not IsValid) is the sign they are ready,
                // since SetIntendedMode dereferences it.
                if (movement == null || movement.SpeedModule == null) return;

#if DEBUG
                ReportDrift(movement, unscaledDeltaTime);
                ReportStamina(unscaledDeltaTime);
#endif

                if (TryRelocate(movement)) return;

                movement.SetIntendedMode(ToMoveMode(_state.MoveMode));
                movement.SetDirection(_state.Moving ? Direction(_state.MoveAngle) : Vector2.zero);

                // A sender that never turned reports +X.
                Body.GetLookModule().SetLookDirection(Direction(_state.LookAngle));

                ApplyHostility();
                ApplyPassability();
                ApplyDowned();
                ApplyHealth();

                // Before the action, so a swing or block is judged against this frame's pool.
                ApplyStamina();

                // Position follows while down, but stance and actions belong to the knockout.
                Follow(movement, unscaledDeltaTime);
                if (_knockedOut != null) return;

                ApplyStance();
                ApplyAction(unscaledDeltaTime);
                AlignBlockWindow();
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Puppet] applying intent to {Id} failed: " + e);
            }
        }

        /// <summary>
        /// Takes the body off the floor by hand, doing what <c>RemoveKnockout</c> may skip (knockout flag, look module, mode).
        /// </summary>
        private void StandUp()
        {
            if (Body.CustomStats != null && Body.CustomStats.IsKnockout)
            {
                Body.CustomStats.SetKnockoutMode(false);
            }

            Actor.LookModule? look = Body.GetLookModule();
            if (look != null)
            {
                look.Enabled = true;
            }

            Actor.InteractionModeModule? stance =Body.GetInteractionModule();
            if (stance != null && stance.CurrentMode == Actor.EInteractionMode.KnockOut)
            {
                stance.RequestMode(Actor.EInteractionMode.Normal);
            }
        }

        /// <summary>
        /// Gives the body its owner's build and voice, since a clone keeps the prefab's build. <c>None</c> means unread and is skipped.
        /// </summary>
        private void ApplyBuild(EquipmentSnapshot snapshot)
        {
            try
            {
                ActorBodyType? build = Body.GetActorGender();
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
        /// Requests the owner's stance through <c>RequestMode</c>, so a drawn weapon plays its draw animation. Only on a difference.
        /// </summary>
        private void ApplyStance()
        {
            if (!TryReadStance(_state.InteractionMode, out Actor.EInteractionMode wanted)) return;

            // The floor is ApplyDowned's. A player who just got up still reports KnockOut until their queued stand-up
            // begins, which would floor this body right after it stood.
            if (wanted == Actor.EInteractionMode.KnockOut) return;

            Actor.InteractionModeModule? stance = Body.GetInteractionModule();
            if (stance == null) return;

            if (stance.TargetMode == wanted) return;

            stance.RequestMode(wanted);
        }

        /// <summary>Reads a stance off the wire, refusing modes this build does not know.</summary>
        private static bool TryReadStance(byte value, out Actor.EInteractionMode mode)
        {
            mode = (Actor.EInteractionMode)value;
            return Enum.IsDefined(mode);
        }

        /// <summary>
        /// Keeps this body's hostility in step with the friendly fire rule. Every frame, since the rule and the local player can change.
        /// </summary>
        private void ApplyHostility()
        {
            // Per pair, so the same body can be hit inside the arena and not outside it.
            bool harm = Arena.AllowsHarm(Id);
            string? what = PuppetFactory.SetHostility(Body, harm, ref _hostility);
            if (what != null) MelonLogger.Msg($"[Puppet] {Id} is {what}.");

            what = PuppetFactory.SetLocalHostility(Body, harm, ref _localHostility);
            if (what != null) MelonLogger.Msg($"[Puppet] {Id} is {what}.");
        }

        /// <summary>Takes this body's enemy mark off the local player, before the body goes.</summary>
        internal void ReleaseLocalHostility()
        {
            try
            {
                PuppetFactory.SetLocalHostility(Body, false, ref _localHostility);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Puppet] taking a departed body's enemy mark off the local player failed: " + e);
            }
        }

        /// <summary>
        /// Keeps this body passable on a timer, since the game re-solidifies it in many places (RemoveKnockout among them).
        /// </summary>
        private void ApplyPassability()
        {
            _sincePassable += Time.unscaledDeltaTime;
            if (_isPassable && _sincePassable < PassabilityRefreshSeconds) return;

            _sincePassable = 0f;

            // Same timer: the hit loader switches hittability off when the camera is far, and the noticeable component wires up late.
            PuppetFactory.KeepHittable(Body);
            PuppetFactory.KeepNoticeable(Body);

            // The AI adapter is built lazily. Returns at once once the name is no longer the shared one.
            PuppetTargets.GiveOwnIdentity(Body);

            if (!PuppetFactory.MakePassable(Body)) return;

            // Logged once, when it first takes.
            if (!_isPassable)
            {
                _isPassable = true;

                MelonLogger.Msg($"[Puppet] {Id} can be walked through and no longer blocks anybody. " +
                    "The colliders stay live as triggers, which is what the enemy target scan reads.");
            }
        }

        /// <summary>
        /// BrawlActor.Knockout without raising KnockoutEvent, whose observers subscribe by the player's EntityInfo
        /// and would take a teammate's fall for the local player's.
        /// </summary>
        private void KnockOutQuietly(BrawlActor brawler)
        {
            brawler._isInKnockout = true;
            Body.CustomStats.SetKnockoutMode(true);
            Body.GetStatusEffectHandler()?.RemoveAllDamageEffects();
            Body.CustomStats.SetBrawlSoftAggroState(false);
            Body.GetLookModule().Enabled = false;
            Body.AIModule.StopAI();
            Body.GetActionModule().ClearCurrentAndQueuedActions();
            Body.GetAnimatorModule().InterruptHitFrame();
            Body.GetInteractionModule().RequestMode(Actor.EInteractionMode.KnockOut);
            Body.AIModule.DisableRVOController();
            Body.GetColliderModule().DeactivateCollision();
            Body.GetHitReceiveHandler().DisableCollider();
        }

        /// <summary>
        /// Knocks this body out when its owner is down and stands it up when they rise, through the game's own BrawlActor.
        /// <see cref="PuppetFactory.Constrain"/> then undoes what RemoveKnockout re-enables.
        /// </summary>
        private void ApplyDowned()
        {
            bool wanted = _state.Downed;
            if (wanted == (_knockedOut != null)) return;

            try
            {
                if (wanted)
                {
                    BrawlActor brawler = new(Body);
                    KnockOutQuietly(brawler);
                    _knockedOut = brawler;
                    ArenaTeammates.KnockedDown(Body);

                    MelonLogger.Msg($"[Puppet] {Id} is down.");
                    return;
                }

                // RemoveKnockout silently refuses a body that reads as dead or keeps its knockout state, and a puppet can be either
                // (_isDead never clears). So the state is cleared first and the stand-up asserted, not requested.
                Health? health = Body.GetHealth();
                bool wasDead = health != null && health.IsDead;
                bool wasKeeping = Body.CustomStats != null && Body.CustomStats.KeepKnockoutState;

                if (health != null && wasDead)
                {
                    health._isDead = false;
                }

                if (wasKeeping)
                {
                    Body.CustomStats.KeepKnockoutState = false;
                }

                using (RemoteApply<HealthScope>.Enter())
                {
                    _knockedOut!.RemoveKnockout();
                }

                // Re-asserted regardless, since RemoveKnockout may have returned early. The mode is only requested.
                StandUp();

                _knockedOut = null;
                _appliedHealth = -1;
                PuppetFactory.Constrain(Body);

                MelonLogger.Msg($"[Puppet] {Id} is back up." +
                    (wasDead || wasKeeping
                        ? $" The game would have refused it: dead={wasDead}, keepKnockout={wasKeeping}."
                        : string.Empty));
            }
            catch (Exception e)
            {
                // Cleared either way. A body stuck mid-knockout would never be driven again.
                _knockedOut = wanted ? _knockedOut : null;
                MelonLogger.Error($"[Puppet] could not put {Id} {(wanted ? "down" : "back up")}: " + e);
            }
        }

        /// <summary>
        /// Advances this body's block clock by one round trip, once per block instance, so both machines reach the same
        /// perfect-block verdict. The two disagree by exactly 2d (one-way delay d), which is the measured round trip.
        /// </summary>
        private void AlignBlockWindow()
        {
            Actor.ActionModule? actions = Body.GetActionModule();
            if (actions == null || !actions.HasCurrentAction)
            {
                _alignedBlock = IntPtr.Zero;
                return;
            }

            AA_ABase? current = actions.CurrentAction;

            // The same bind is a block only when the far player has a shield in hand.
            AA_BlockAction? block = current == null ? null : current.TryCast<AA_BlockAction>();
            if (block == null)
            {
                _alignedBlock = IntPtr.Zero;
                return;
            }

            // Pooled instances compared by pointer. Re-advancing the clock every frame would close the window instantly.
            HoldPerfectWindow(block);

            if (block.Pointer == _alignedBlock) return;
            _alignedBlock = block.Pointer;

            // No measurement, no correction.
            float? roundTrip = CoopSession.RoundTripMs(Id);
            if (roundTrip == null) return;

            block._blockTime += roundTrip.Value / 1000f;
        }

        /// <summary>
        /// Holds this body's perfect-block window open while its owner's is. The setter is gated on a talent the puppet lacks,
        /// so the field is written directly.
        /// </summary>
        private void HoldPerfectWindow(AA_BlockAction block)
        {
            bool open = _hasPerfectBlock && block._blockTime <= block._perfectBlockTimeWindow.Seconds;

            if (block._isInPerfectBlockWindow == open) return;

            block._isInPerfectBlockWindow = open;
        }

        /// <summary>
        /// Puts the far player's stamina on their body every frame, since AA_BlockAction.CalcDamageAfterStamina decides guard breaks from it.
        /// Written to the field to skip ChangeStamina's events and clamping.
        /// </summary>
        private void ApplyStamina()
        {
            if (_maxStamina == 0) return;

            // Only with a controller of its own. A shared one is the local player's. See PuppetFactory.OwnStaminaOf.
            StaminaController? stamina = PuppetFactory.OwnStaminaOf(Body);
            if (stamina == null) return;

            stamina._maxStamina = _maxStamina;
            stamina._curStamina = _state.Stamina;
        }

        /// <summary>
        /// Puts the far player's health on their body with SetHealth, only on change since CheckEvents fires events.
        /// Never zero, since <c>_isDead</c> latches for good.
        /// </summary>
        private void ApplyHealth()
        {
            // Zero means no vitals heard yet, not a player with none left.
            if (_maxHealth == 0) return;

            Health? health = Body.GetHealth();
            if (health == null) return;

            // The owner's maximum too, which levels and mods change, by runtime delta.
            if (health.MaxHealth != _maxHealth)
            {
                using (RemoteApply<HealthScope>.Enter())
                {
                    health.AddRuntimeMaxHealth(_maxHealth - health.MaxHealth);
                }

                _appliedHealth = -1;
            }

            int wanted = _state.Health < 1 ? 1 : _state.Health;
            if (wanted == _appliedHealth) return;

            _appliedHealth = wanted;

            // So the health funnel hook does not echo every replicated figure back to its owner.
            using (RemoteApply<HealthScope>.Enter())
            {
                health.SetHealth(wanted, null);
            }
        }

        /// <summary>
        /// Starts and releases actions on the body through the game's own action module, as its owner does.
        /// </summary>
        private void ApplyAction(float unscaledDeltaTime)
        {
            Actor.ActionModule? actions =Body.GetActionModule();
            if (actions == null) return;

            bool isNew = !_hasAppliedAction
                || PlayerStateMessage.IsNewerAction(_state.ActionSequence, _appliedActionSequence);

            if (!isNew)
            {
                RetryPending(actions, unscaledDeltaTime);
                ApplyRelease(actions, unscaledDeltaTime);
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

            // Released by the remembered name when the body is told to do something else.
            // The same action arriving twice is a combo and must not be released between swings.
            if (_activeAction != wanted && _activeAction != Actor.ActionModule.EActionBind.None)
            {
                actions.TryReleaseAction(_activeAction);
                _activeAction = Actor.ActionModule.EActionBind.None;
            }

            if (wanted == Actor.ActionModule.EActionBind.None)
            {
                _pendingAction = Actor.ActionModule.EActionBind.None;

                // An action no button started (mining, fishing, work at a spot), named by prefab. Started once per use.
                if (_state.ActionPrefab != 0 && ActionPrefabIndex.TryResolve(_state.ActionPrefab, out AA_ABase prefab))
                {
                    // The spot gave its owner this action, so it is made here. Idempotent.
                    actions.GetOrCreateActionInstanceFromPrefab(prefab, null, null);
                    Actor.ActionModule.EStartResult started = actions.TryStartAction(prefab);
#if DEBUG
                    MelonLogger.Msg($"[Act] {Id} start {prefab.name} seq {_state.ActionSequence}: {started}");
#endif
                }

                return;
            }

            _pendingAction = wanted;
            _pendingIsCombo = _state.ActionCombo && _activeAction == wanted;
            _pendingSeconds = _pendingIsCombo ? PendingComboSeconds : PendingActionSeconds;
            _releaseApplied = false;
            _releaseWaitsForStart = false;

            RetryPending(actions, unscaledDeltaTime);
            ApplyRelease(actions, unscaledDeltaTime);
        }

        /// <summary>
        /// Lets go of the button when its owner did, separate from the action ending, or a tap plays as a charged attack.
        /// </summary>
        private void ApplyRelease(Actor.ActionModule actions, float unscaledDeltaTime)
        {
            if (_releaseApplied) return;
            if (!_state.ActionReleased) return;
            if (_activeAction == Actor.ActionModule.EActionBind.None) return;

            // A queued swing has not started. TryReleaseAction would end the previous swing and the queued one plays as a charge.
            if (_releaseWaitsForStart)
            {
                _releaseWaitSeconds -= unscaledDeltaTime;

                if (ActionBinds.Current(actions).IsSameUse(_releaseWaitFrom))
                {
                    // Given up on rather than applied late, since releasing now would hit the wrong swing.
                    if (_releaseWaitSeconds <= 0f)
                    {
                        _releaseWaitsForStart = false;
                        _releaseApplied = true;
                    }

                    return;
                }

                _releaseWaitsForStart = false;
            }

            actions.TryReleaseAction(_activeAction);
            _releaseApplied = true;
        }

        /// <summary>
        /// Starts the far player's action, retrying briefly since this body runs behind and may miss the combo window
        /// (<c>AA_AttackAction.CanComboStartedNow</c>). Bounded, so it cannot land on a swing the player never made.
        /// </summary>
        private void RetryPending(Actor.ActionModule actions, float unscaledDeltaTime)
        {
            if (_pendingAction == Actor.ActionModule.EActionBind.None) return;

            _pendingSeconds -= unscaledDeltaTime;

            // Sampled before and after, since the result code does not say whether anything happened.
            ActionBinds.Snapshot before = ActionBinds.Current(actions);

            // A combo step waits for the combo window before asking, since every start request spends the chain's single
            // chance (AA_Atk_Combo: NUM_CHANCES_TO_TRIGGER = 1).
            if (_pendingIsCombo && before.Bind == _pendingAction && IsBeforeComboWindow(actions))
            {
                if (_pendingSeconds > 0f) return;

                MelonLogger.Warning($"[Puppet] {Id} combo step on {_pendingAction} waited {PendingComboSeconds:0.0}s " +
                    "for a combo window that never opened; dropped.");
                _pendingAction = Actor.ActionModule.EActionBind.None;
                return;
            }

            // The mirror case: a fresh swing arriving inside this body's window would play as a combo. It waits for the window to close.
            if (!_pendingIsCombo && before.Bind == _pendingAction && IsInComboWindow(actions))
            {
                _pendingSeconds = Math.Max(_pendingSeconds, 0.1f);
                return;
            }

            // A late combo step is let back into the window's last phase, since in the return it would queue as a fresh swing.
            if (_pendingIsCombo && before.Bind == _pendingAction)
            {
                AA_AttackAction? attack = actions.CurrentAction.TryCast<AA_AttackAction>();
                if (attack != null && attack.CurrentPhase == AA_AttackAction.Phase.Return) attack.SetPhase((int)AA_AttackAction.Phase.Linger);
            }

            // tryToQueue is true, the game's default, since a combo arrives while the first swing still runs.
            Actor.ActionModule.EStartResult result = actions.TryStartAction(_pendingAction);

            // A started combo returns OtherActionInProgress, the same code as a real refusal.
            // So the body is compared instead of the result.
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

                // Unchanged means queued behind the running swing, so the release must wait for it to start.
                _releaseWaitsForStart = after.IsSameUse(before);
                _releaseWaitFrom = after;
                _releaseWaitSeconds = ReleaseWaitSeconds;

                _activeAction = _pendingAction;
                _pendingAction = Actor.ActionModule.EActionBind.None;
                return;
            }

            if (_pendingSeconds > 0f) return;

            // Logged every time. The game's reason tells a clone bug from a legitimate refusal.
            MelonLogger.Warning($"[Puppet] {Id} could not start {_pendingAction} within " +
                $"{PendingActionSeconds:0.00}s: {result}.");

            _pendingAction = Actor.ActionModule.EActionBind.None;
        }

        /// <summary>Whether the swing this body runs is inside its combo window, where a start continues the chain.</summary>
        private static bool IsInComboWindow(Actor.ActionModule actions)
        {
            AA_AttackAction? attack = actions.HasCurrentAction ? actions.CurrentAction.TryCast<AA_AttackAction>() : null;
            return attack != null && attack.HasCombo && attack.CanComboStartedNow();
        }

        /// <summary>Whether the swing this body runs has not reached its combo window yet (still winding up or held).</summary>
        private static bool IsBeforeComboWindow(Actor.ActionModule actions)
        {
            AA_AttackAction? attack = actions.HasCurrentAction ? actions.CurrentAction.TryCast<AA_AttackAction>() : null;
            if (attack == null) return false;

            AA_AttackAction.Phase phase = attack.CurrentPhase;
            return phase == AA_AttackAction.Phase.None || phase == AA_AttackAction.Phase.Antp || phase == AA_AttackAction.Phase.Hold;
        }

        /// <summary>
        /// Eases the body to its owner's position, frame-rate independent. A clone has no input controller, so it cannot walk itself.
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
        /// <summary>Notes both stamina bars when a remote action starts, so the follow-up can say which one moved.</summary>
        private void NoteStamina()
        {
            _localStaminaBefore = LocalStamina();
            _puppetStaminaBefore = PuppetStamina();
            _staminaReportIn = StaminaReportDelay;
        }

        /// <summary>
        /// Says who paid for the remote player's last action. The local player's bar falling is the bug, the puppet's is correct.
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
            return PlayerAccess.TryGetPlayer(out Actor player)
                ? Stamina(player)
                : float.NaN;
        }

        private float PuppetStamina()
        {
            return Body != null ? Stamina(Body) : float.NaN;
        }

        private static float Stamina(Actor actor)
        {
            StaminaController? controller = actor.GetStaminaController();
            return controller != null ? controller.CurStamina : float.NaN;
        }

        /// <summary>
        /// Reports the gap to the owner's position when above <see cref="DriftReportDistance"/>.
        /// </summary>
        private void ReportDrift(Actor.MovementModule movement, float unscaledDeltaTime)
        {
            _sinceDriftReport += unscaledDeltaTime;
            if (_sinceDriftReport < 3f) return;

            float drift = Vector3.Distance(Body.transform.position, Authoritative());
            if (drift < DriftReportDistance) return;

            // Reset only once something was said.
            _sinceDriftReport = 0f;

            MelonLogger.Msg($"[Puppet] {Id} drift={drift:0.0} desiredSpeed={movement.GetDesiredSpeed():0.0} " +
                $"moving={_state.Moving} mode={_state.MoveMode}");
        }
#endif

        /// <summary>Moves the body outright when the gap is too large to be drift (a teleport, a scene change, lost contact).</summary>
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

        /// <summary>Turns a wire value into a move mode, clamping modes this build does not know.</summary>
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
