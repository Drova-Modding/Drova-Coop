using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
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

namespace DrovaCoopMod.World
{
    /// <summary>
    /// One remote player: the body shown for them, split between what it looks like and where it is.
    ///
    /// Intent decides appearance. The direction, move mode and look direction the far player's input
    /// layer produced are handed to the same modules the local player's input drives, so the body faces
    /// the right way and plays the right animation without any of that being reimplemented.
    ///
    /// The wire decides position. Intent alone cannot move a clone: it has no input controller, and
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

        private ushort _maxHealth;

        private ushort _maxStamina;

        // Whether this player can perfect-block at all. The window is gated on the talent and a puppet
        // carries none, so without being told this body could never enter it.
        private bool _hasPerfectBlock;
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

        /// <summary>
        /// How long a release waits for the swing it belongs to to actually begin.
        ///
        /// Long enough to outlast the swing it is queued behind, which is the whole point of waiting;
        /// short enough that a queue which never drains is given up on rather than left to release
        /// something the player did much later.
        /// </summary>
        private const float ReleaseWaitSeconds = 1.5f;

        private Actor.ActionModule.EActionBind _activeAction = Actor.ActionModule.EActionBind.None;
        private Actor.ActionModule.EActionBind _pendingAction = Actor.ActionModule.EActionBind.None;
        private float _pendingSeconds;
        private bool _releaseApplied;

        // Set when a start was accepted without anything beginning - a queued swing. The snapshot is what
        // the body was running at that moment, so a change away from it is the queued swing starting.
        private bool _releaseWaitsForStart;
        private ActionBinds.Snapshot _releaseWaitFrom;
        private float _releaseWaitSeconds;
        private byte _appliedActionSequence;
        private bool _hasAppliedAction;

        // Which equipment message this body is already wearing. Zero is "nothing has arrived", and the
        // session counts from one, so a body dresses itself the first time it hears anything.
        private int _appliedEquipmentVersion;

        /// <summary>
        /// How long a body may be too busy to be dressed before that is worth a line. Long enough that an
        /// ordinary swing or two passes unremarked, short enough to name the problem in the same session
        /// it happens.
        /// </summary>
        private const float EquipmentBlockedWarningSeconds = 8f;

        private float _equipmentBlockedSeconds;
        private int _warnedEquipmentBlocked = -1;

        // Which cast announcement this body has already performed, and what it is currently holding. The
        // held item is kept because the release names the item being let go, and by the time it arrives
        // the owner may have selected something else entirely.
        private int _appliedItemUseVersion;
        private byte _appliedItemUseSequence;
        private bool _hasAppliedItemUse;
        private Item? _itemInUse;

        // What this body was last set to, so an unchanged figure is not written again. Negative is "never
        // heard", which no health value can be.
        private int _appliedHealth = -1;

        // The decoration that makes the local player this body's enemy, or null when there is none. Held
        // rather than rebuilt, because taking it off again needs the same reference that was put on.
        private IAlignment? _hostility;
        private bool _isPassable;
        private float _sincePassable;

        /// <summary>
        /// How often this body is made passable again. Short enough that a teammate who has just been
        /// helped up is a brief obstacle rather than a lasting one, long enough to cost nothing.
        /// </summary>
        private const float PassabilityRefreshSeconds = 2f;

        // The knockout this body is in, or null when it is on its feet. Non-null is also the answer to
        // "is this body down", so there is no second flag to keep in step with it.
        private BrawlActor? _knockedOut;

        // The block instance whose clock has already been slid back by a round trip, as its native
        // address. Zero when the body is not blocking. See AlignBlockWindow.
        private IntPtr _alignedBlock;

#if DEBUG
        /// <summary>
        /// How far the body has to be from where its owner says it is before the gap is worth a log line.
        ///
        /// Well above what following normally leaves - the body chases the authoritative position at
        /// <see cref="FollowRate"/> and a running player stays inside about a unit of it - and far below
        /// <see cref="TeleportDistance"/>, which is where the gap stops being drift and gets snapped shut.
        /// Between those two is the range where something is wrong and nothing has noticed.
        /// </summary>
        private const float DriftReportDistance = 3f;

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
        /// Whether this player's own game says they are on the floor. Read off the state they send rather
        /// than off the knockout this machine built from it, so it is their answer and not this one.
        /// </summary>
        internal bool IsDown => _state.Downed;

        /// <summary>
        /// Puts on what its owner is wearing, if that has changed since the last time.
        ///
        /// This decides which attack the body performs, not only what it looks like. Equipping a
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

            // Not in the frame the body was built. Gear put on an actor that has not finished
            // initializing is accepted by the equipment module and then attaches to nothing, so the body
            // stands there holding an invisible weapon: the slots say it is armed and the screen says it
            // is not. Left unconsumed, so the outfit is applied the moment the body is ready.
            //
            // This only became reachable when bodies started being rebuilt after a world reload - see
            // RemotePlayers.DropBodiesThatDidNotSurvive. A body built when its player joins is dressed a
            // good many frames later, when the first equipment message arrives, and was never at risk.
            if (!Body.IsInitialized) return;

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
                if (actions != null && actions.HasCurrentAction)
                {
                    // Said once per outfit, and only after waiting a while. Silence here is
                    // indistinguishable from equipment that never arrived, and both look like a body
                    // wearing the wrong clothes - so a body that is never idle long enough to be dressed
                    // has to say so rather than simply staying in prefab gear.
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

                // The build first, and before a single item goes on. The skin is applied by the skin
                // item's own equip effect, and all that effect does is set a skin id on whatever build the
                // actor already has - so putting the items on first paints the owner's skin onto the
                // prefab's body, which is what made the hair, beard and scars sit wrong.
                ApplyBuild(snapshot);

                SubDatabase_Item? database = ProviderAccess.ItemDatabase;
                if (database == null)
                {
                    // Left unconsumed on purpose. Marking the version applied here would spend the only
                    // announcement this player will make - equipment is sent on change, not repeated - so
                    // a database that was a frame late once would leave the body in prefab gear for the
                    // rest of the session.
                    MelonLogger.Warning("[Equip] no item database yet; the body stays as it is for now.");
                    return;
                }

                _appliedEquipmentVersion = snapshot.Version;

                equipment.UnequipAll();

                // Kept rather than given up on. A refusal is usually about order rather than about the
                // item: ActorEquipSlot.CheckRequiredSlot turns a slot type away when the slot it depends on
                // is empty, so arrows are refused until the quiver is on. The wire carries the owner's slot
                // order, which has no reason to be a dependency order, and it routinely puts the ammo
                // before the thing that holds it.
                List<(Item Item, byte Set)> refused = new();

                int dressed = 0;
                for (int index = 0; index < snapshot.ItemIds.Length; index++)
                {
                    string id = snapshot.ItemIds[index];
                    Item item = database.GetItemByReadableId(id);
                    if (item == null)
                    {
                        // A real possibility rather than a defensive check: the other player may be running
                        // a mod that adds items this game does not have. Naming it is the whole value of
                        // the line, because the symptom otherwise is a body missing one piece of gear.
                        MelonLogger.Warning($"[Equip] {Id} has '{id}', which this game does not know. Mod sets may differ.");
                        continue;
                    }

                    // Into the set its owner keeps it in, not into whichever one happens to be in hand.
                    // Both sets are sent, so putting them all in the active one would stack a bow and a
                    // sword into the same two hands and lose whichever did not fit - and the slot that
                    // holds the ammo belongs to every set, which is why it arrives marked as shared.
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

                // Before the second pass rather than after everything, because a dependency is only
                // satisfied by a slot in the active set - CheckRequiredSlot asks
                // GetSlotsInActiveSet - so a quiver sitting in the set that is not in hand answers nothing.
                // Selecting is still late enough for what it is here for: it puts the set's weapons in the
                // body's hands and applies that set's equip effects, and both want the set already filled.
                equipment.TrySelectEquipmentSet(snapshot.ActiveSet);

                dressed += DressAgain(equipment, refused);

                foreach ((Item item, byte _) in refused)
                {
                    // Named, not counted. A total told us that some things did not fit and left which ones
                    // to guesswork; the slot type is what says whether a whole category is being refused
                    // or one particular item is.
                    item.TryGetItemBehaviour<ItemBhvr_IEquippable>(out ItemBhvr_IEquippable? equippable);
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
        /// Puts one item on the body, in the set its owner keeps it in.
        ///
        /// True is replaceLast: when no slot of the item's type is free, put it in the last one rather than
        /// giving up. On a body being dressed to match somebody else there is no such thing as a slot worth
        /// protecting - whatever is in the way is a leftover of the gear this body is being taken out of.
        /// </summary>
        private static bool TryEquip(Actor.EquipmentModule equipment, Item item, byte set)
        {
            return set == PlayerEquipmentMessage.SharedSet
                ? equipment.EquipOnFirstEmptySlotInActiveSet(item, true)
                : equipment.EquipOnFirstEmptySlotInSet(item, set, true);
        }

        /// <summary>
        /// Offers everything that was refused again, until a pass puts nothing on.
        ///
        /// A pass rather than a sort, because the dependency lives on the slot type asset and is only
        /// answered by what is on the body at that moment - so the cheap way to satisfy it is to ask again
        /// once the rest of the outfit is on, and to keep asking while that is still changing the answer.
        /// It terminates because a pass that equips nothing ends the loop and a pass that equips something
        /// shortens the list.
        ///
        /// Worth doing rather than tolerating: a refused item is not only missing gear. Refusing ammo runs
        /// <c>ActorEquipSlot.TriggerNeedsQuiver</c>, which puts "you need a quiver" on screen for whoever
        /// is watching, because a puppet passes <c>EntityGameHandler.CheckIsPlayer</c>.
        /// </summary>
        /// <returns>How many of them went on.</returns>
        private static int DressAgain(Actor.EquipmentModule equipment, List<(Item Item, byte Set)> refused)
        {
            int dressed = 0;
            bool anyWentOn = true;

            while (anyWentOn && refused.Count > 0)
            {
                anyWentOn = false;

                // Backwards, so removing the one that fit does not skip the next.
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
        /// Casts what its owner cast, if that has not been performed yet.
        ///
        /// The item is handed straight to the game, which does everything else: the animation, the flow
        /// cost, the projectile and its impact all come from this machine's own assets, so nothing about
        /// the spell itself has to cross.
        ///
        /// The gate the game's own input layer uses is deliberately not consulted. <c>CanBeUsedBy</c>
        /// is not the harmless question it looks like: on a busy actor it queues an action as a side
        /// effect, on a failed stat requirement it pushes text into this machine's HUD, and on empty flow
        /// it plays the local out-of-flow sound. It would also frequently refuse - the puppet does not
        /// carry the ability item and its flow is not its owner's - and refusing is wrong, because the
        /// caster's own game already decided this cast was allowed. That decision is not this machine's
        /// to second-guess.
        ///
        /// The spell it produces deals no damage. See <see cref="ItemUseWatch"/>: the caster's machine
        /// already resolved this spell against its copy of whoever it hit and sent the result as an
        /// ordinary blow, so the copy spawned here is a picture of that and nothing more.
        /// </summary>
        internal void RecordItemUse(ItemUseSnapshot snapshot)
        {
            if (snapshot.Version == _appliedItemUseVersion) return;
            if (Body == null) return;

            try
            {
                // The release is not a use of its own, so a snapshot that only shows the release still
                // has to start the thing first. Only the newest announcement survives in the session
                // layer, and the game offers the release one frame after the start - so at any real
                // latency the pair routinely arrives in a single poll and collapses into the release.
                // Treating that as "let go of nothing" is what made abilities and potions silently fail to
                // appear. Asking whether the sequence is new instead means a collapsed pair performs the
                // use and then ends it, which is exactly what a tap is.
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

        /// <summary>
        /// Puts the item in the body's hands and starts using it.
        /// </summary>
        /// <returns>False when the game is not ready to be asked yet, in which case nothing is consumed and
        /// the attempt is made again next frame.</returns>
        private bool TryStartItemUse(ItemUseSnapshot snapshot)
        {
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;

            // Left unconsumed on purpose, like the equipment does. Marking the version applied here would
            // spend the only announcement this use will ever make, and the database is routinely a frame
            // or two behind a world that has just been rebuilt.
            if (database == null) return false;

            Item item = database.GetItemByReadableId(snapshot.ItemId);
            if (item == null)
            {
                // A real possibility rather than a defensive check: the other player may be running a mod
                // that adds abilities this game does not have. Naming it is the whole value of the line,
                // because the symptom otherwise is a player who visibly does nothing.
                MelonLogger.Warning($"[Use] {Id} used '{snapshot.ItemId}', which this game does not know. Mod sets may differ.");

                // Consumed rather than retried: an id this game has never heard of will not appear in the
                // database a frame later, and retrying it forever would log the same line forever.
                return true;
            }

            // A use that was never released, because its release was lost or because the body was not here
            // yet. Left holding, the next one would never start on some abilities.
            ReleaseItemUse();

            _itemInUse = item;

            // Aimed abilities do not use the direction the body is facing if there is a custom aim
            // direction set, and on a real player that field is rewritten from the mouse every single
            // frame. Nothing writes it deliberately on a puppet, so whatever it holds is a leftover - and
            // a leftover is not a small error: it is a fixed wrong direction for every aimed spell this
            // body ever casts, pointing wherever the value came from. Zeroed, the game falls back to the
            // facing, which is the direction that actually crossed the wire.
            Actor.LookModule look = Body.GetLookModule();
            if (look != null)
            {
                look.SetCustomAbilityDir(Vector2.zero);
            }

            // The guard, so the postfix that watches the local player's own uses does not read this one as
            // something this machine's player did. It checks the actor as well, so this is the second of
            // two answers rather than the only one - which is the point, because the first stops working
            // the moment a puppet is ever mistaken for the player.
            // Named against this body, so anything the spell conjures can be attributed to this player's
            // cast rather than to nobody. The conjure does not happen inside this call - the spell's action
            // spawns its ability from a phase trigger, frames later - so the name has to outlive it. See
            // SummonWatch.
            SummonWatch.BeginCast(Id, snapshot.Sequence, Body);

            using (RemoteApply<ItemUseScope>.Enter())
            {
                item.StartUseItem(Body);
            }

            return true;
        }

        /// <summary>
        /// Takes the count of a cast without performing it.
        ///
        /// For a body that has just been built into a session already in progress: the newest cast may
        /// have been announced long before this body existed, and performing it now would be a spell out
        /// of nowhere rather than a spell that was missed.
        /// </summary>
        internal void AdoptItemUse(ItemUseSnapshot snapshot)
        {
            _appliedItemUseVersion = snapshot.Version;
            _appliedItemUseSequence = snapshot.Sequence;
            _hasAppliedItemUse = true;
        }

        /// <summary>
        /// Lets go of whatever this body is holding, if anything. Safe to call when it is holding nothing.
        /// </summary>
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
        /// Takes what the far player's bars are out of. Arrives on change, not per tick, so it is kept
        /// apart from the state and outlives any number of them.
        /// </summary>
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
                movement.SetDirection(_state.Moving ? Direction(_state.MoveAngle) : Vector2.zero);

                // A look angle always points somewhere; a sender that never turned reports +X, which is
                // the way a body that never turned faces anyway.
                Body.GetLookModule().SetLookDirection(Direction(_state.LookAngle));

                ApplyHostility();
                ApplyPassability();
                ApplyDowned();
                ApplyHealth();

                // Before the action, not after. A body whose pool still held last frame's figure could
                // refuse a swing its owner has already paid for, and a block resolved against a stale
                // pool answers the guard-break question with the wrong number.
                ApplyStamina();

                // Position still follows while down - the owner can be pushed around on the floor, and a
                // body that stopped tracking would drift for as long as the knockout lasts. Stance and
                // actions do not: the knockout owns both, and driving them from the stream would fight it
                // for the mode and start swings the owner cannot possibly be making.
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
        /// Takes the body off the floor, by hand.
        ///
        /// Every step here is something <c>RemoveKnockout</c> also does and may have skipped. The
        /// knockout flag is what the animator reads, so a body left with it set keeps playing the downed
        /// pose no matter what its stance says; the look module is switched off on the way down and
        /// nothing else switches it back on; and the mode is what actually moves the body out of the
        /// pose.
        ///
        /// Requested rather than set instantly, because a stance change is a transition the game runs
        /// over time and <see cref="ApplyStance"/> will correct it to whatever the owner is really in on
        /// the next packet - <c>Normal</c> is only the way out of <c>KnockOut</c>, not a claim about
        /// where the owner ended up.
        /// </summary>
        private void StandUp()
        {
            if (Body.CustomStats != null && Body.CustomStats.IsKnockout)
            {
                Body.CustomStats.SetKnockoutMode(false);
            }

            Actor.LookModule look = Body.GetLookModule();
            if (look != null)
            {
                look.Enabled = true;
            }

            Actor.InteractionModeModule stance = Body.GetInteractionModule();
            if (stance != null && stance.CurrentMode == Actor.EInteractionMode.KnockOut)
            {
                stance.RequestMode(Actor.EInteractionMode.Normal);
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
        /// Equipping a weapon is not holding it. A body that owns a spear but is in <c>Normal</c> has
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
        /// Keeps this body's hostility in step with the session's friendly fire rule.
        ///
        /// Reconciled every frame rather than set once at creation, for two reasons that both come down to
        /// the same thing: the answer can change after the body exists. The host can flip the rule
        /// mid-session, and a body built before the local player's actor was ready had nothing to be
        /// hostile to yet. Comparing state to intent every frame covers both without either of them
        /// needing to know about the other.
        ///
        /// Cheap when nothing changed - one reference comparison inside
        /// <see cref="PuppetFactory.SetHostility"/>, which returns immediately.
        /// </summary>
        private void ApplyHostility()
        {
            // Per pair rather than per session, so a body standing in the arena with this player can be
            // hit and the same body outside it cannot. Reconciled every frame already, which is what
            // makes walking in and out of the circle work without anything else being told.
            string? what = PuppetFactory.SetHostility(Body, Arena.AllowsHarm(Id), ref _hostility);
            if (what == null) return;

            MelonLogger.Msg($"[Puppet] {Id} is {what}.");
        }

        /// <summary>
        /// Keeps this body out of everybody's way, and keeps it that way.
        ///
        /// The state does not stick, which is why this is a timer rather than a one-off.
        /// <c>ActivateCollision</c> is the matching call to the one that makes a body passable, and the
        /// game makes it in plenty of places - the one this mod triggers itself is
        /// <c>BrawlActor.RemoveKnockout</c>, so every time a remote player is helped up their body turns
        /// solid again. Re-asserting is two field writes against an actor's two body colliders, which is
        /// cheap enough that a timer beats trying to enumerate everything in the game that might have
        /// undone it.
        /// </summary>
        private void ApplyPassability()
        {
            _sincePassable += Time.unscaledDeltaTime;
            if (_isPassable && _sincePassable < PassabilityRefreshSeconds) return;

            _sincePassable = 0f;

            // On the same timer, because they are undone by the same kinds of thing and both are a few
            // field writes. Being hittable is switched off by the hit system's own loader whenever the
            // camera is far away, and being noticeable is granted by a pooled component whose wiring can
            // arrive after the body does - so neither is settled by having been done once at creation.
            PuppetFactory.KeepHittable(Body);
            PuppetFactory.KeepNoticeable(Body);

            // The AI adapter is built lazily, so the body can exist for a while before there is anything to
            // write the name onto. Idempotent: it returns at once once the name is no longer the shared one.
            PuppetTargets.GiveOwnIdentity(Body);

            if (!PuppetFactory.MakePassable(Body)) return;

            // Once, when it first takes. The refresh runs for the life of the session and has nothing to
            // say when it changes nothing.
            if (!_isPassable)
            {
                _isPassable = true;

                MelonLogger.Msg($"[Puppet] {Id} can be walked through and no longer blocks anybody. " +
                    "The colliders stay live as triggers, which is what the enemy target scan reads.");
            }
        }

        /// <summary>
        /// Puts this body on the floor when its owner is, and stands it up when they do.
        ///
        /// The game's own knockout on both machines, rather than the stance alone. The stance byte does
        /// move into <c>KnockOut</c> by itself, which is why a downed player used to look roughly right
        /// here - but it is only the pose. Nothing clears the queued actions, nothing stops the body being
        /// a target, and getting up again is left to a stance packet competing with everything else that
        /// writes that module. <c>BrawlActor</c> does all of it, and does it identically at both ends.
        ///
        /// Standing up costs more than it looks: <c>RemoveKnockout</c> restarts the AI, re-activates the
        /// body collision and re-enables the hit receiver, all correct for the NPC it was written for and
        /// all wrong for a puppet. <see cref="PuppetFactory.Constrain"/> says again what this body is.
        ///
        /// It also heals the actor to a tenth of its maximum on the way out, because a puppet does not
        /// read as the player to <c>EntityGameHandler.CheckIsPlayer</c>. That figure is this machine's
        /// invention and its owner's next packet overrules it - but only if the health applier is told to
        /// stop believing what it last wrote, which is what clearing the applied value does.
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
                    brawler.Knockout();
                    _knockedOut = brawler;

                    MelonLogger.Msg($"[Puppet] {Id} is down.");
                    return;
                }

                // The game's own stand-up is refused for a body like this one, silently. Everything
                // that takes an actor off the floor sits behind two tests inside RemoveKnockout: the
                // actor must not read as dead, and it must not be keeping its knockout state. A puppet
                // can fail either. Its health is this mod's invention, and _isDead latches at zero and is
                // cleared by nothing in the game, so a body ever written to zero can be knocked down for
                // the rest of the session and never stood up again. Knockout itself has no such test,
                // which is exactly why going down kept working while coming back up did not.
                //
                // So the state is cleared first and the stand-up is then asserted rather than requested.
                Health health = Body.GetHealth();
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

                // Said again regardless of what the game just did. These are the three things that keep a
                // body on the floor, and re-asserting them costs nothing when RemoveKnockout already did
                // them - they are all idempotent - while being the whole difference when it returned
                // early. The mode is only requested, not forced, so the stream's own stance still wins.
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
                // Cleared either way. A body stuck believing it is mid-knockout would never be driven
                // again, which is a worse outcome than one that is briefly in the wrong pose.
                _knockedOut = wanted ? _knockedOut : null;
                MelonLogger.Error($"[Puppet] could not put {Id} {(wanted ? "down" : "back up")}: " + e);
            }
        }

        /// <summary>
        /// Moves this body's perfect-block window back by one round trip, so that both machines reach the
        /// same verdict about the same swing.
        ///
        /// The two disagree by exactly one round trip, and the arithmetic says so. Write one-way
        /// delay as <c>d</c>, the far player's block starting at wall time <c>T</c>, and the local swing
        /// being committed at <c>s</c> with a windup of <c>w</c>:
        ///
        /// - Over there, this machine's swing arrives at <c>s+d</c> and lands at <c>s+d+w</c>. Their
        ///   verdict is whether <c>s+d+w</c> falls inside <c>[T, T+window]</c>.
        /// - Over here, their block only starts at <c>T+d</c> and the swing lands at <c>s+w</c>. This
        ///   verdict is whether <c>s+w</c> falls inside <c>[T+d, T+d+window]</c> - the same as asking
        ///   whether <c>s+w-d</c> falls inside <c>[T, T+window]</c>.
        ///
        /// One asks about <c>s+w+d</c>, the other about <c>s+w-d</c>. The gap is <c>2d</c>, which is the
        /// round trip - and the round trip is a number this session already measures for the latency
        /// display. Advancing the body's own block clock by it slides the window here back onto the one
        /// over there.
        ///
        /// This is not lag compensation and does not pretend to be. Nothing is rewound and nobody is
        /// given back the information they lost; the defender still reacts to a swing the attacker
        /// committed to a round trip ago, and no correction here can change that. All it removes is the
        /// case where the two machines resolve one swing and disagree about what happened - the defender
        /// parries and the attacker's game says an ordinary block, which is unfair in the plain sense
        /// that the same event had two outcomes.
        ///
        /// Done once per block instance, on the first frame the body is seen holding one. The game clears
        /// the window itself on its next update, comparing the clock against
        /// <c>_perfectBlockTimeWindow</c> - so the flag, the stat modifications hanging off it and the
        /// event that removes them all still go through the game's own path. A round trip wider than the
        /// window simply means the body is never seen in it, which is the honest answer at that latency.
        /// </summary>
        private void AlignBlockWindow()
        {
            Actor.ActionModule actions = Body.GetActionModule();
            if (actions == null || !actions.HasCurrentAction)
            {
                _alignedBlock = IntPtr.Zero;
                return;
            }

            AA_ABase current = actions.CurrentAction;

            // TryCast rather than a type test: what is running is decided by the native object, and the
            // same bind is a block only when the far player has a shield in hand.
            AA_BlockAction? block = current?.TryCast<AA_BlockAction>();
            if (block == null)
            {
                _alignedBlock = IntPtr.Zero;
                return;
            }

            // By pointer, and instances are pooled, so this is "the same block still running" rather than
            // "a block that looks like the last one". Re-advancing an already advanced clock every frame
            // would close the window instantly.
            HoldPerfectWindow(block);

            if (block.Pointer == _alignedBlock) return;
            _alignedBlock = block.Pointer;

            // No measurement yet means no correction. Guessing a round trip would be worse than the
            // uncorrected window, which is at least wrong by a known amount.
            float? roundTrip = CoopSession.RoundTripMs(Id);
            if (roundTrip == null) return;

            block._blockTime += roundTrip.Value / 1000f;
        }

        /// <summary>
        /// Keeps this body's perfect-block window open for as long as its owner's is.
        ///
        /// The game will not do it. Opening the window runs through a setter gated on the blocker having
        /// learned the talent, and a puppet is stripped of talents, so on a remote body the flag is never
        /// written at all - not opened, and not closed either. Every perfect block its owner lands would
        /// read here as an ordinary one: the wrong spark, and the wrong one of the two status effects a
        /// block lays on whoever swung.
        ///
        /// Written straight to the field rather than through the setter, which is the gate. Nothing else
        /// is lost by that: what hangs off the setter's event is a stat modification on a body whose
        /// stats are its owner's business.
        ///
        /// The clock this reads has already been slid back by a round trip, so the window it describes is
        /// the one the owner is actually in rather than the one this machine would have guessed.
        /// </summary>
        private void HoldPerfectWindow(AA_BlockAction block)
        {
            bool open = _hasPerfectBlock && block._blockTime <= block._perfectBlockTimeWindow.Seconds;

            if (block._isInPerfectBlockWindow == open) return;

            block._isInPerfectBlockWindow = open;
        }

        /// <summary>
        /// Puts the far player's stamina on their body.
        ///
        /// Not cosmetic. Drova resolves a block by asking the blocker's own controller whether it can
        /// afford the hit - <c>AA_BlockAction.CalcDamageAfterStamina</c> - so this figure is what decides
        /// whether the guard holds or breaks on this machine. It used to be answered by infinite stamina,
        /// which always said yes; see <see cref="PuppetFactory"/> for why that had to go.
        ///
        /// Written to the field rather than through <c>ChangeStamina</c>, which raises full and empty
        /// events, runs the block-damage fixup and clamps against a maximum this body may not share.
        /// Thirty times a second, none of that is wanted: the owner has already run all of it on the
        /// machine where it counts, and what is left to do here is store the answer.
        ///
        /// Every frame rather than on change, and deliberately not guarded by a last-written value. The
        /// body spends this pool itself now - it runs its owner's actions for real - so unlike health,
        /// which nothing here writes but this, the number moves underneath us between two packets.
        /// </summary>
        private void ApplyStamina()
        {
            if (_maxStamina == 0) return;

            // Only when this body has a controller to itself. A shared one is the local player's, and
            // writing it would take the stamina of the person playing and hand it to whatever the far
            // player last reported - see PuppetFactory.OwnStaminaOf.
            StaminaController? stamina = PuppetFactory.OwnStaminaOf(Body);
            if (stamina == null) return;

            stamina._maxStamina = _maxStamina;
            stamina._curStamina = _state.Stamina;
        }

        /// <summary>
        /// Puts the far player's health on their body.
        ///
        /// Written with <c>Health.SetHealth</c>, which is the one path that stores a figure as given:
        /// <c>ChangeHealth</c> would run the number through this machine's damage calculation - defender
        /// stats, blockers, invincibility, a crit roll - and arrive somewhere else. The owner has already
        /// decided; there is nothing here to compute.
        ///
        /// Only when it differs from the last one written, because <c>SetHealth</c> is not inert: it ends
        /// in <c>CheckEvents</c>, which raises the health-changed, damaged and healed events and can tag
        /// the actor into combat. Rewriting an unchanged figure thirty times a second would fire all of
        /// that thirty times a second for a player standing still.
        ///
        /// Never to zero, deliberately. <c>CheckEvents</c> latches <c>_isDead</c> on the way past and
        /// nothing in the game ever clears it - there is no assignment of <c>false</c> to it anywhere in
        /// the assembly. Death in Drova is not a health value being reset afterwards; it is prevented
        /// beforehand by a damage blocker, and coming back is <c>BrawlActor.RemoveKnockout</c> on the
        /// knockout system. So a body told its owner reached zero would be permanently dead here while
        /// its owner carried on playing, and a floor of one is the honest placeholder until that system
        /// is replicated rather than approximated.
        /// </summary>
        private void ApplyHealth()
        {
            // Zero is what a sender with no health component puts on the wire, and what a body that has
            // not heard its player's vitals yet holds; neither is a player who has none left.
            if (_maxHealth == 0) return;

            Health health = Body.GetHealth();
            if (health == null) return;

            int wanted = _state.Health < 1 ? 1 : _state.Health;
            if (wanted == _appliedHealth) return;

            _appliedHealth = wanted;

            // The guard matters even though nothing watches health yet. Slice two patches the change
            // funnel to broadcast, and this write goes through that funnel - so without it, the first
            // hook added would echo every replicated figure straight back at its owner.
            using (RemoteApply<HealthScope>.Enter())
            {
                health.SetHealth(wanted, null);
            }
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
            _releaseWaitsForStart = false;

            RetryPending(actions, unscaledDeltaTime);
            ApplyRelease(actions, unscaledDeltaTime);
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
        private void ApplyRelease(Actor.ActionModule actions, float unscaledDeltaTime)
        {
            if (_releaseApplied) return;
            if (!_state.ActionReleased) return;
            if (_activeAction == Actor.ActionModule.EActionBind.None) return;

            // A queued swing has not started, and releasing now would let go of the wrong one.
            // TryReleaseAction names a bind, and the module hands that to whatever is running - so a
            // release applied while the request is still queued behind the previous swing ends the
            // previous swing, which was going to end anyway, and leaves the queued one to start with
            // nothing having asked it to stop. The game reads a swing nobody let go of as a charge, so a
            // tap the far player made arrives here as a power attack. Waiting for the swing to actually
            // begin is what keeps a tap a tap.
            if (_releaseWaitsForStart)
            {
                _releaseWaitSeconds -= unscaledDeltaTime;

                if (ActionBinds.Current(actions).IsSameUse(_releaseWaitFrom))
                {
                    // Given up on rather than applied late. If the queued swing never started there is
                    // nothing to let go of, and releasing anyway would be the very mistake this exists to
                    // avoid.
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
        /// Tries to start the action the far player began, and keeps trying briefly if the body is not
        /// ready for it yet.
        ///
        /// A combo has a window, and this body is behind. A combo may only begin during the signal,
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

            // A started combo reports itself as a refusal. Inside the module, a swing that continues a
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

                // Whether anything actually began, which is a different question from whether the request
                // was accepted. An unchanged body means the module took the request and queued it behind
                // the swing already running; the release for it has to wait until that queue drains, or it
                // lands on the wrong swing and turns a tap into a charge.
                _releaseWaitsForStart = after.IsSameUse(before);
                _releaseWaitFrom = after;
                _releaseWaitSeconds = ReleaseWaitSeconds;

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
            StaminaController controller = actor.GetStaminaController();
            return controller != null ? controller.CurStamina : float.NaN;
        }

        /// <summary>
        /// Reports how far the body is from where its owner says it is, and how fast it is being told to
        /// move. Steady drift growing at the sender's own speed means the body is not translating at all,
        /// which looks identical to working replication until something measures it.
        ///
        /// Only when the gap is worth reading. The unconditional version was written when the question was
        /// "does this translate at all", and it answered that - but it also buried every other line in the
        /// log under a number that reads the same every three seconds once replication works. A gap under
        /// <see cref="DriftReportDistance"/> is the follow rate doing its job, not news.
        /// </summary>
        private void ReportDrift(Actor.MovementModule movement, float unscaledDeltaTime)
        {
            _sinceDriftReport += unscaledDeltaTime;
            if (_sinceDriftReport < 3f) return;

            float drift = Vector3.Distance(Body.transform.position, Authoritative());
            if (drift < DriftReportDistance) return;

            // Only reset once something was actually said, so a gap that opens up between two ticks is
            // reported on the next frame rather than waiting out the rest of an interval it spent healthy.
            _sinceDriftReport = 0f;

            MelonLogger.Msg($"[Puppet] {Id} drift={drift:0.0} desiredSpeed={movement.GetDesiredSpeed():0.0} " +
                $"moving={_state.Moving} mode={_state.MoveMode}");
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
