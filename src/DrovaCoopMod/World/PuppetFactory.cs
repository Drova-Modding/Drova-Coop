using Drova_Modding_API.Access;
using DrovaCoopMod.Session;
using Il2CppAssets.Scripts.Entity.Modules;
using Il2CppDrova;
using Il2CppDrova.Alignment;
using Il2CppDrova.Cognition;
using Il2CppDrova.FlowSystem;
using Il2CppDrova.HitSystem;
using Il2CppDrova.Saveables;
using Il2CppInterop.Runtime;
using Il2CppDrova.StaminaSystem;
using Il2CppDrova.Utilities.LazyLoading;
using Il2CppOpenWorldSystem;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Builds the body a remote player is shown as: a clone of the game's own player prefab, stripped of
    /// everything that would make it a second player as far as the save system is concerned.
    ///
    /// The recipe is the game's own, read out of <c>PlayableSceneSetup.SetupScene</c>. It lives here
    /// rather than in the diagnostic that first proved it, because getting it wrong corrupts saves and
    /// two copies of it would eventually disagree.
    /// </summary>
    internal static class PuppetFactory
    {
        // Said once. The condition is a property of how the game builds a body, so it holds for every
        // puppet in the session and a line per body would only repeat it.
        private static bool _warnedAboutSharedStamina;
        private static bool _warnedAboutSharedFlow;

        /// <summary>
        /// Clones the player prefab into an inert body positioned at <paramref name="position"/>.
        /// </summary>
        /// <param name="name">Scene object name, so a puppet is identifiable in a hierarchy dump.</param>
        /// <param name="position">Where to put it.</param>
        /// <param name="puppet">The new body, or null when the prefab could not be found.</param>
        /// <returns>True when a puppet was created.</returns>
        internal static bool TryCreate(string name, Vector3 position, out Actor? puppet)
        {
            puppet = null;

            Actor? prefab = FindPlayerPrefab();
            if (prefab == null)
            {
                MelonLogger.Error("[Puppet] PlayableSceneSetup._playerPrefab not found; cannot create a remote player body.");
                return false;
            }

            // Instantiated under an inactive parent so the clone is inactive in the hierarchy and Unity
            // never runs its Awake. That is the whole trick: the save components have to be gone before
            // they ever start, because ASaveRoot.InitRoots sweeps GetComponentsInChildren and the player
            // save key is the hardcoded constant "Player#" - a second live one reads and writes the real
            // player's save bucket.
            GameObject holder = new("DrovaCoop.PuppetHolder");
            holder.SetActive(false);

            Actor created = Object.Instantiate(prefab, holder.transform);
            created.gameObject.name = name;

            int stripped = StripSaveables(created.gameObject);

            // Also while inactive, and for the same reason as the strip: the reference point registers
            // itself with the streaming root in OnEnable, so what it is configured as has to be settled
            // before Unity ever enables it.
            string streaming = EnsureStreaming(created.gameObject);

            // Reparented out of the holder before activating, so the holder can go and the puppet lives in
            // the scene like anything else.
            created.transform.SetParent(null, worldPositionStays: false);
            created.transform.position = position;
            created.gameObject.SetActive(true);

            Object.Destroy(holder);

            Constrain(created);

            MelonLogger.Msg($"[Puppet] created '{name}' at {position}, {stripped} save components stripped, {streaming}.");

            puppet = created;
            return true;
        }

        /// <summary>
        /// Makes a body behave like a puppet rather than like an actor: no AI of its own, solid to
        /// everything except the person playing, hittable but not write-authoritative over its own
        /// health, and free to act.
        ///
        /// Applied at creation and again whenever the game undoes it. <c>BrawlActor.RemoveKnockout</c> is
        /// the case that matters: standing an actor up restarts its AI, re-activates its body collision
        /// and re-enables the hit receiver, because that is exactly right for the NPC it was written for.
        /// On a puppet it hands back three of the four things creation took away, so the way to keep a
        /// knockout the game's own is to let it run and then say again what this body is.
        ///
        /// Every step is idempotent, which is what makes calling it repeatedly safe.
        /// </summary>
        internal static void Constrain(Actor puppet)
        {
            DeactivateAI(puppet);
            MakePassable(puppet);
            MakeHittable(puppet);
            KeepNoticeable(puppet);
            PuppetTargets.GiveOwnIdentity(puppet);
            StopPayingForActions(puppet);
            StopPayingForCasts(puppet);
            HideBorrowedInterface(puppet);
        }

        /// <summary>
        /// Takes away the interface the clone inherited from the player prefab.
        ///
        /// A player's own HUD is a child of the player, not something the scene's canvas owns.
        /// <c>CS_Action_ShowStaminaBar</c> finds the stamina bar with
        /// <c>GetComponentInChildren&lt;GUI_StaminaBar&gt;</c> and toggles the object;
        /// <c>GUI_AmmoBar</c> holds a serialized <c>Actor</c> that Unity remaps to the clone when the
        /// prefab is instantiated. Cloning therefore clones the whole thing, and the copy draws at the same
        /// place on screen as the real one: two sets of bars under the character, the lower one reporting a
        /// player who is somewhere else entirely.
        ///
        /// Both were invisible until the thing they report started moving on a remote body, which is why
        /// they survived so many sessions. A puppet that could not spend stamina was always full, and
        /// <c>GUI_AmmoBar.CheckVisible</c> shows nothing until the body is in combat mode holding something
        /// that needs ammo.
        ///
        /// Switched off at the canvas rather than per widget, because the list of widgets is not
        /// knowable from here - it is whatever the player prefab was authored with, and the next one added
        /// to the game would arrive as another report of a bar drawn over the local player. A puppet has no
        /// business drawing any of this player's interface, so the whole inherited canvas goes.
        ///
        /// The component, not the object. The bars stay alive and wired, so nothing that holds a reference
        /// to one finds it missing, and the game's own switches still do what they say - they simply have
        /// nothing to draw into.
        ///
        /// Showing a teammate's stamina and ammunition is still worth doing. It is a job for something that
        /// draws over their body deliberately, next to their name, not for a second copy of this player's
        /// HUD.
        /// </summary>
        private static void HideBorrowedInterface(Actor puppet)
        {
            try
            {
                List<string> hidden = new();

                foreach (Canvas canvas in puppet.gameObject.GetComponentsInChildren<Canvas>(true))
                {
                    if (canvas == null || !canvas.enabled) continue;

                    canvas.enabled = false;
                    hidden.Add(canvas.gameObject.name);
                }

                if (hidden.Count > 0)
                {
                    // Named rather than counted, because which canvases a player carries is the thing
                    // nobody can look up: it is authored in the prefab, and this log line is where it is
                    // written down.
                    MelonLogger.Msg($"[Puppet] {hidden.Count} inherited canvas(es) switched off; they draw " +
                        "over the local player: " + string.Join(", ", hidden));
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not hide the inherited interface: " + e.Message);
            }
        }

        /// <summary>
        /// Destroys a puppet.
        /// </summary>
        internal static void Destroy(Actor? puppet)
        {
            if (puppet == null) return;

            StopBeingNoticeable(puppet);

            // The reference point is switched off before the body goes, rather than left to the
            // destruction to deal with. Disabling runs OnDisable now and takes the point out of the
            // streaming root's list immediately; Destroy is deferred to the end of the frame, and a root
            // that walks its list in between finds a component whose transform is already gone.
            try
            {
                foreach (OW_ReferencePoint point in puppet.gameObject.GetComponentsInChildren<OW_ReferencePoint>(true))
                {
                    if (point != null)
                    {
                        point.enabled = false;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not unregister the reference point: " + e.Message);
            }

            Object.Destroy(puppet.gameObject);
        }

        /// <summary>
        /// Finds the player prefab the game itself spawns. <c>PlayableSceneSetup</c> is a
        /// ScriptableObject, so it is reachable as a loaded asset rather than needing a live scene object -
        /// which is also why <c>FindObjectsOfTypeAll</c> is the right call here and its usual hazard,
        /// returning assets as well as instances, is exactly what is wanted.
        /// </summary>
        private static Actor? FindPlayerPrefab()
        {
            foreach (PlayableSceneSetup setup in Resources.FindObjectsOfTypeAll<PlayableSceneSetup>())
            {
                Actor prefab = setup._playerPrefab;
                if (prefab != null)
                {
                    return prefab;
                }
            }

            return null;
        }

        /// <summary>
        /// Destroys every save component on the clone.
        ///
        /// Both hierarchies have to go, and they are unrelated: <c>ASaveRoot</c> owns an object's save
        /// bucket, <c>ASaveableMonoBehaviourBase</c> writes into one, and neither derives from the other.
        /// Matching on base type rather than on names is what catches <c>SaveRoot_LazyActorGuid</c>, which
        /// is on the player prefab and is not the one anybody thinks to look for.
        ///
        /// The API's <c>NoSaveGuidRegistry</c> cannot be used instead: it excludes by instance key, and
        /// the player's key is the constant "Player#" shared with the real player, so registering it would
        /// switch off the real player's saving too.
        /// </summary>
        private static int StripSaveables(GameObject root)
        {
            int stripped = 0;

            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;

                bool isSaveable = component.TryCast<ASaveRoot>() != null
                    || component.TryCast<ASaveableMonoBehaviourBase>() != null;

                if (!isSaveable) continue;

                Object.DestroyImmediate(component);
                stripped++;
            }

            return stripped;
        }

        /// <summary>
        /// Makes the world load around the body, not only around whoever is local.
        ///
        /// Drova streams the world in chunks, and what decides which chunks are up is <c>OW_Root</c>'s list
        /// of <c>OW_ReferencePoint</c>s: it unions a radius around every one of them. That the list is a
        /// list rather than a single point is the whole reason two players in different places is possible
        /// at all - nothing here has to teach the engine anything, only to add the second point.
        ///
        /// <c>_isEssential</c> is not a refinement. <c>OW_Root._chunkPerfMode</c> is static and process-wide,
        /// and under <c>ChunkPerfMode.Load</c> non-essential points are skipped outright - so a puppet
        /// without the flag streams correctly right up until the game decides it is under memory pressure,
        /// and then silently stops, which is the kind of bug that only ever shows up on somebody else's
        /// machine.
        ///
        /// The player prefab brings no point of its own - checked, rather than assumed either way - so the
        /// added branch is the one taken today. The inherited branch is kept because a body that did bring
        /// one must reuse it: a second point on the same body would ask the root to union a radius with
        /// itself. Either way it is reconfigured, since the flags a point was authored with are the ones
        /// that suit whatever it was authored for.
        /// </summary>
        /// <returns>What was done, for the creation log: which of the two cases this was is the thing to
        /// know when the world does not stream and somebody has to work out why.</returns>
        private static string EnsureStreaming(GameObject root)
        {
            try
            {
                OW_ReferencePoint? point = root.GetComponentInChildren<OW_ReferencePoint>(true);
                bool inherited = point != null;

                if (point == null)
                {
                    // Non-generic AddComponent: the generic overload has to resolve a native generic
                    // instantiation, and this one only needs the type, which always exists.
                    point = root.AddComponent(Il2CppType.Of<OW_ReferencePoint>())?.TryCast<OW_ReferencePoint>();
                    if (point == null)
                    {
                        return "no reference point could be added, so the world will not stream around this body";
                    }
                }

                point._isEssential = true;

                // Never `_autoFindRoot`, however much it looks like the field for this. The point's
                // OnEnable registers with every root in `_defaultRoots` and, if that flag is set, with
                // `OW_Root.CurrentRoot` - but its OnDisable only ever removes it from `_defaultRoots`. A
                // point found automatically is therefore never unregistered, so destroying the body leaves
                // the root holding a component whose transform is gone, and `HasAnyRefDataPointChanged`
                // throws on it every frame from then until the game is closed. Naming the root in the list
                // instead means registration and removal go through the same collection and match.
                point._autoFindRoot = false;
                point._defaultRoots = new Il2CppSystem.Collections.Generic.List<OW_Root>();

                OW_Root currentRoot = OW_Root.CurrentRoot;
                if (currentRoot == null)
                {
                    return "no streaming root to register with, so the world will not stream around this body";
                }

                point._defaultRoots.Add(currentRoot);

                // The authored default holds a point inert until its transform leaves the position it
                // started at. A remote body starts on top of the local player and is then carried to where
                // its owner is, so it would come good on the first packet - but a body whose owner is
                // standing still elsewhere would sit there streaming nothing, which is precisely the case
                // this exists for.
                point._disableUntilMoved = false;
                point.enabled = true;

                return inherited
                    ? "reference point inherited from the prefab and made essential"
                    : "essential reference point added";
            }
            catch (Exception e)
            {
                // Not fatal. A body that does not stream is a body standing in an unloaded world, which is
                // worth having and worth complaining about, but it is not worth refusing to show a player.
                MelonLogger.Warning("[Puppet] could not set up chunk streaming: " + e.Message);
                return "chunk streaming could not be set up";
            }
        }

        /// <summary>
        /// Takes the body out of everything that decides where things can walk, without taking it out of
        /// the physics world.
        ///
        /// This used to disable every collider on the body, because a remote body is placed where its
        /// owner says it is and that placement does not consult collision - it cannot, since the owner has
        /// already decided where they are. A solid body moved that way is a moving wall: it pushes
        /// whatever it lands on, and what it usually lands on is the local player, who ends up shoved
        /// through geometry into places they cannot walk back out of.
        ///
        /// It also made the body invisible to every enemy in the game, which is the reason this now
        /// works the other way round. Drova's AI finds targets with
        /// <c>RaycastTargetActorCollector</c>: a <c>Physics2D.CircleCastNonAlloc</c> over the dynamic
        /// layers, keeping only hits whose own transform carries an <c>Actor</c> - so the collider the
        /// scan needs is the one on the actor's root, exactly the one being switched off. That is what
        /// produced the strangest symptom in play: a remote player could pull an enemy into combat, since
        /// aggro comes from <c>Health.TagCombatActor</c> and needs no collider, but was never chosen as
        /// the thing to attack, so every enemy they woke walked past them to the nearest local player.
        ///
        /// So the colliders stay live and the pushing is answered where it happens, by taking the one
        /// pair that matters out of the simulation. <c>Physics2D.IgnoreCollision</c> is per pair, so an
        /// NPC still walks into this body and paths around it, which is what makes it a real obstacle in
        /// a fight rather than something enemies stand inside.
        ///
        /// Made a trigger rather than disabled, and rather than paired off against the local player.
        /// The distinction decides whether this works at all, because Drova does not let Unity's solver
        /// move actors. <c>EntityRigidbody.HandleMovement</c> sweeps the body itself with
        /// <c>Rigidbody2D.Cast</c> against a <c>ContactFilter2D</c> and depenetrates with
        /// <c>Physics2D.Distance</c> - both scene queries, and a scene query does not consult
        /// <c>Physics2D.IgnoreCollision</c>, which filters contact generation only. Pairing the two bodies
        /// therefore stops a puppet shoving a player who is standing still, and does nothing about a
        /// player who is walking: their own sweep still finds the body, clamps against it and pushes them
        /// out of it. A teammate would be a wall.
        ///
        /// A trigger is invisible to all of that, because every movement filter in the game is authored
        /// with <c>useTriggers</c> off - and it stays visible to the AI's target scan, which is a
        /// <c>Physics2D.CircleCastNonAlloc</c> taking a layer mask and no filter, so it answers to the
        /// project's <c>queriesHitTriggers</c>, which is on. Solid to nobody, still findable by everything
        /// that hunts.
        ///
        /// This is the game's own idiom rather than an invention: <c>IEntityColliderModule</c> is asked to
        /// do it, and <c>DeactivateCollision</c> is what the game itself calls for a body being carried, in
        /// dialogue, or riding something. It covers both of an actor's body colliders - the root one and
        /// the physical one hanging off it - and leaves the hit system alone, which is what keeps a remote
        /// player hittable.
        ///
        /// Re-asserted rather than done once, because <c>ActivateCollision</c> is the matching call and
        /// plenty of things make it: <c>BrawlActor.RemoveKnockout</c> is the one this mod triggers itself.
        /// </summary>
        /// <returns>True when the body was made passable; false when it has no collider module to ask.</returns>
        internal static bool MakePassable(Actor puppet)
        {
            // Kinematic as well: a dynamic body integrates gravity and velocity between the frames that
            // place it, which fights the placement and can drift it off a ledge.
            foreach (Rigidbody2D rigidbody in puppet.gameObject.GetComponentsInChildren<Rigidbody2D>(true))
            {
                if (rigidbody == null) continue;

                rigidbody.bodyType = RigidbodyType2D.Kinematic;
            }

            IEntityColliderModule colliders = puppet.GetColliderModule();
            if (colliders == null) return false;

            // Repaired before it is made passable, because the two flags are separate and only one of
            // them is this mod's doing. `DeactivateCollision` writes `isTrigger` and never touches
            // `enabled`, so anything in the game that reaches for `DeactivateComponent` - the unstuck
            // handler is the one a constantly repositioned body will meet - leaves a collider that no
            // longer exists as far as the physics scene is concerned. That is not "solid" or "passable",
            // it is gone: the AI's target scan is a `Physics2D.CircleCastNonAlloc`, and a disabled
            // collider is never returned by one, so the body stops being something enemies can choose.
            Collider2D body = colliders.GetCollider();
            if (body != null && !body.enabled)
            {
                body.enabled = true;

                MelonLogger.Msg("[Puppet] the body's collider had been switched off; switched back on. " +
                    "While it is off no enemy can pick this body as a target.");
            }

            colliders.DeactivateCollision();
            return true;
        }

        /// <summary>
        /// Keeps a remote player's body something a blow can land on.
        ///
        /// The hit system unloads by distance to the camera, and the camera is here. Every
        /// <c>HitReceiveHandler</c> registers itself with the lazy loader, which drops it once the camera
        /// is more than three hundred units outside the receiver's own bounds - and dropping it runs
        /// <code>
        /// receiveCollider.enabled = false;
        /// rigidbody.simulated = false;
        /// </code>
        /// On the host that is true of a teammate's body the whole time the two players are apart, and it
        /// is why a creature was seen swinging at a client who took nothing from it while the host watched
        /// their own health fall. <see cref="CameraReach"/> stops the loader reaching that decision; this
        /// undoes a decision already reached, which matters for a body built before the session's first
        /// remote player existed and for anything else in the game that unloads a receiver.
        ///
        /// <c>EnableCollider</c> is not enough on its own and never was: it activates the object the
        /// collider sits on, which is a different flag from the collider's own, and the loader writes the
        /// other one.
        ///
        /// Receivers that only ever check the player are left alone. Those are switched off at
        /// <c>Start</c> by design and answer through a hand-rolled overlap test instead, so simulating
        /// them would put a collider into the world that the game deliberately keeps out of it.
        ///
        /// Silent, because it is re-asserted on a timer for the life of the session and has nothing to
        /// say on the runs where it changes nothing.
        /// </summary>
        internal static void KeepHittable(Actor puppet)
        {
            try
            {
                foreach (HitReceiveHandler handler in puppet.gameObject.GetComponentsInChildren<HitReceiveHandler>(true))
                {
                    if (handler == null || handler._onlyCheckWithPlayer) continue;

                    handler.EnableCollider();

                    Collider2D receiver = handler.GetReceiveCollider();
                    if (receiver != null)
                    {
                        receiver.enabled = true;
                    }

                    Rigidbody2D rigidbody = handler.GetRigidbody();
                    if (rigidbody != null)
                    {
                        rigidbody.simulated = true;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not keep the body hittable: " + e.Message);
            }
        }

        /// <summary>
        /// Puts a remote player's body into the list of things creatures can notice.
        ///
        /// Being perceivable is not a property of being an actor. It is granted by a
        /// <c>HitTriggerBhvr_ActorCognition</c>, which registers the body with
        /// <c>CognitionOctreeManager</c> in its <c>OnEnable</c>.
        ///
        /// The player prefab does carry one - read out of the prefab, since nothing in the decompiled code
        /// says so either way - and a clone therefore inherits it with its actor reference already baked
        /// in, so a puppet registers itself. This is the belt to that pair of braces rather than the fix
        /// for a missing registration: the component can also arrive from a pool, whose ordering enables
        /// it before it is told which actor it belongs to, and a body that lost the race would be one no
        /// creature ever reacts to however close it stands.
        ///
        /// Safe to say twice: the manager's list refuses a duplicate, so a body the game already
        /// registered is added once and no more.
        ///
        /// What it protects is the behavior trees' own noticing node, which reads the list this feeds
        /// rather than testing geometry - unlike <c>IsPerceivingActor</c>, which does its own arithmetic
        /// and never consults it.
        /// </summary>
        internal static void KeepNoticeable(Actor puppet)
        {
            try
            {
                IActorCognitionModule cognition = puppet.CognitionModule;
                if (cognition == null) return;

                ICognitionObject self = cognition.Actor;
                if (self == null) return;

                if (!CognitionOctreeManager.TryGet(out CognitionOctreeManager manager) || manager == null) return;

                manager.AddCognition(self);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not make the body noticeable: " + e.Message);
            }
        }

        /// <summary>
        /// Takes a body back out of the list of things creatures can notice.
        ///
        /// Before the destruction rather than left to it, for the same reason the reference point is: the
        /// manager walks its list every frame and asks each entry where it is, and an entry whose actor
        /// has gone answers with a transform that no longer exists.
        /// </summary>
        private static void StopBeingNoticeable(Actor puppet)
        {
            try
            {
                IActorCognitionModule cognition = puppet.CognitionModule;
                ICognitionObject? self = cognition?.Actor;
                if (self == null) return;

                if (!CognitionOctreeManager.TryGet(out CognitionOctreeManager manager) || manager == null) return;

                manager.RemoveCognition(self);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not stop the body being noticeable: " + e.Message);
            }
        }

        /// <summary>
        /// Lets the body be struck without letting the strike decide anything.
        ///
        /// Two halves that look unrelated and are not. The hit system finds a target through
        /// <c>HitTriggerHandler.OnTriggerEnter2D</c>, which asks the collider it touched for an
        /// <c>IHitReceiver</c> - so the receive collider has to be live, and it is switched on here rather
        /// than merely left alone, because the handler's own disable path deactivates the object it sits
        /// on. That alone would make the body take damage on this machine, computed from this machine's
        /// swing against a body whose health only ever arrives over the wire: two authorities for one
        /// number.
        ///
        /// Invincibility settles it, and it settles it without hiding anything, because of how
        /// <c>Health.ChangeHealth</c> is written:
        /// <code>
        /// if (healthChange.IsHealingOrZero || (!_isInvincible &amp;&amp; ...)) { _curHealth += resultingDamage; }
        /// HealthChangeArgs changeArgs = new HealthChangeArgs(this, healthChange, curHealth, resultingDamage, ...);
        /// </code>
        /// The flag skips the write and nothing else. The damage is still calculated, still handed to
        /// <c>CheckEvents</c> with the full figure, and still reaches anything watching - so the number
        /// this machine computed can be reported to the player it happened to, while their body here
        /// holds whatever they last said it holds.
        ///
        /// It also keeps the body permanently eligible. <c>ChangeHealth</c> refuses outright once
        /// <c>IsDead</c> is set, and that flag is never cleared anywhere in the game, so a body that was
        /// once allowed to reach zero would stop registering hits for the rest of the session.
        /// </summary>
        private static void MakeHittable(Actor puppet)
        {
            try
            {
                int enabled = 0;

                foreach (HitReceiveHandler handler in puppet.gameObject.GetComponentsInChildren<HitReceiveHandler>(true))
                {
                    if (handler == null || handler._onlyCheckWithPlayer) continue;

                    enabled++;
                }

                // Both flags, not only the game's own switch. See KeepHittable: the switch activates the
                // object the collider sits on, and the lazy loader writes the collider's own flag and the
                // rigidbody's, which are what decide whether a blow can reach this body at all.
                KeepHittable(puppet);

                Health health = puppet.GetHealth();
                if (health == null)
                {
                    MelonLogger.Warning("[Puppet] no health component; the body cannot be hit and cannot report damage.");
                    return;
                }

                health.SetInvinciblity(true);

                // Whether the local player may actually land a swing is decided separately and repeatedly,
                // by RemotePlayer, because it is a rule the host can change while the body already exists.
                MelonLogger.Msg($"[Puppet] {enabled} hit receivers live, health write-protected.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not make the body hittable: " + e.Message);
            }
        }

        /// <summary>
        /// Makes the local player's swings count against this body.
        ///
        /// Live hit colliders are not enough on their own. A receiver runs its filters before it
        /// accepts anything, and an actor carries a <c>HitFilter_Alignment</c> configured with
        /// <c>Enemy</c> and <c>Neutral</c> - the two states in which being struck is legitimate. Two
        /// players are neither: they are friendly to each other, the filter returns false, and
        /// <c>HitReceiveHandler.Check</c> folds that into its result with <c>&amp;=</c>, so the swing is
        /// discarded before any damage is calculated. Nothing about that failure is visible; the attack
        /// simply passes through.
        ///
        /// So the body is told to regard the local player as an enemy, using the game's own decorator for
        /// exactly this - the same one <c>HealthBhvr_OnDmgGetAggro</c> installs when something is hit by a
        /// creature it had no quarrel with. The filter checks both directions and ORs them, which is why
        /// decorating this body is sufficient and the real player is left alone: nothing here changes who
        /// the person playing is at war with.
        ///
        /// The <c>Entity</c> overload, never the <c>EntityInfo</c> one. A puppet is a clone of the player
        /// prefab and therefore shares its info asset, so matching on info would make every player an
        /// enemy of every other body including their own.
        ///
        /// Reversible, and it has to be: friendly fire is a rule the host can change while this body is
        /// already standing in the world. Taking the decoration back is what turning the rule off means,
        /// and it needs the very instance that was added - <c>AlignmentService.RemoveAlignment</c> matches
        /// decorators by reference, so a freshly built equivalent would remove nothing.
        /// </summary>
        /// <param name="puppet">The body to decorate.</param>
        /// <param name="hostile">Whether the local player should count as this body's enemy.</param>
        /// <param name="applied">
        /// The decoration currently on this body, or null when there is none. Updated to match what was
        /// done, so the caller can hand the same reference back to undo it.
        /// </param>
        /// <returns>What was done, for the log, or null when nothing needed doing.</returns>
        internal static string? SetHostility(Actor puppet, bool hostile, ref IAlignment? applied)
        {
            if (hostile == (applied != null)) return null;

            IActorAlignmentModule alignment = puppet?.AlignmentModule!;
            if (alignment == null)
            {
                return "no alignment module, so hits stay filtered out";
            }

            if (!hostile)
            {
                alignment.RemoveAlignment(applied);
                applied = null;
                return "no longer hostile to the local player; swings will be filtered out again";
            }

            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer) || localPlayer == null)
            {
                // Not an error and not final: the caller asks again next frame, and a world that has no
                // player yet is a world that is still loading.
                return null;
            }

            // Cast rather than a C# conversion: interop classes carry no implicit conversion to the
            // interfaces they implement, and only the IL2CPP type system knows this one really does. Kept,
            // rather than built and forgotten, because removing it later needs this exact reference.
            applied = new EnemyAlignment(localPlayer).Cast<IAlignment>();
            alignment.AddDecoratorAlignment(applied);

            return "hostile to the local player so its hit filters accept a swing";
        }

        /// <summary>
        /// Stops the body spending stamina, so that the only thing which moves its pool is the wire.
        ///
        /// This used to switch infinite stamina on, so that a remote body running its owner's swings
        /// could not spend a resource it does not own. That was right about the ownership and wrong about
        /// the consequence, because infinite stamina is not a neutral setting - it short-circuits the one
        /// calculation that decides whether a block holds:
        /// <code>
        /// if (stamina.HasEnoughStamina(num, ChangeType.Block) || _isInPerfectBlockWindow) { dmgValue = 0f; }
        /// </code>
        /// and <c>HasEnoughStamina</c> returns true unconditionally when the flag is set, while
        /// <c>ChangeStamina</c> snaps the pool back to full. A blocking puppet therefore held its guard
        /// against everything forever, on a machine whose player was hitting it - while its owner's real
        /// guard broke on schedule. Two machines, two answers, and the wrong one on the attacker's screen.
        ///
        /// And switching it off was worse. Overwriting the pool every frame does not stop the body
        /// spending it: an action's cost lands inside the frame, drives the figure to zero before the next
        /// write puts it back, and every one of those raises <c>StaminaZeroEvent</c>. Which is heard - a
        /// remote body swinging played the out-of-stamina sound at the local player, on a loop, with its
        /// world bar sinking to empty and refilling thirty times a second. Actions that need stamina can
        /// fail on this machine and succeed on their owner's, too.
        ///
        /// So the flag goes back on and the guard-break disagreement stays open. It belongs with the
        /// arena work, where the honest fix is the defender telling the attacker what their block did
        /// rather than the attacker recomputing it from a pool it was never given.
        ///
        /// The displayed figure is not lost by this: <c>RemotePlayer</c> still writes the owner's value
        /// onto the field afterwards, which is what a teammate's bar should read. Writing the field is
        /// silent, where spending it is not - that difference is the whole trick.
        ///
        /// Kept as an explicit write rather than left to the prefab, because the value has to be certain
        /// in both directions after a knockout: <c>RemoveKnockout</c> runs a good deal of setup, and a
        /// body that came back without it would start spending again.
        ///
        /// The old note is worth keeping for whoever reads the stamina report: a puppet swinging appeared
        /// to drain the watching player's stamina, and the decompiled game does not explain how. The
        /// cost comes from <c>AA_Bhvr_StaminaCondition.ActionStartedEventListener</c>, which spends
        /// <c>action.GetActor().GetStaminaController()</c> - the acting actor's own - and the acting actor
        /// is stamped onto every instance by <c>ActionModule.InstantiateActionInstance</c> from the
        /// module's own actor. There is no global on that path. Either the drain was the on-screen bar
        /// rather than the stamina, or it arrives by a route not yet found; the Debug report in
        /// <c>RemotePlayer</c> names which of the two bars moved and settles it. That report matters
        /// more now than it did, because this change is what would let such a drain through.
        /// </summary>
        private static void StopPayingForActions(Actor puppet)
        {
            try
            {
                StaminaController? stamina = OwnStaminaOf(puppet);
                if (stamina == null) return;

                stamina.SetInfiniteStamina(true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not hand the body's stamina to the wire: " + e.Message);
            }
        }

        /// <summary>
        /// Stops a replicated cast from charging the body that performs it.
        ///
        /// A cast spends flow, and a puppet's flow is not its owner's: the far player already paid for
        /// this spell on their own machine, and the figure this body carries is whatever the prefab
        /// shipped with. Left alone, a body would run itself dry and then silently stop casting, so the
        /// spells a teammate is visibly throwing would stop appearing partway through a fight.
        ///
        /// The change type is refused rather than the pool being made infinite, because the two are not
        /// the same: an infinite pool still runs the change, and a flow change fires the event that
        /// spawns and despawns the aura abilities hanging off flow thresholds. Refusing the type makes
        /// <c>ChangeFlow</c> return before any of that.
        ///
        /// Only the skill type, so nothing else about the body's flow is touched.
        /// </summary>
        private static void StopPayingForCasts(Actor puppet)
        {
            try
            {
                FlowController? flow = OwnFlowOf(puppet);
                if (flow == null) return;

                flow.DisableFlowChangeType(EFlowChangeType.FlowSkill);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not stop the body paying for its casts: " + e.Message);
            }
        }

        /// <summary>
        /// A body's own flow controller, or null when it does not have one to itself.
        ///
        /// Asked for the same reason as <see cref="OwnStaminaOf"/>: a clone that ended up pointing at the
        /// local player's controller would have everything done here land on the person playing, and
        /// refusing their skill flow changes would stop their own spells costing anything.
        /// </summary>
        /// <returns>The controller when it belongs to this body alone; null when it is missing or shared.</returns>
        private static FlowController? OwnFlowOf(Actor? puppet)
        {
            if (puppet == null) return null;

            FlowController flow = puppet.GetFlowBehaviour();
            if (flow == null) return null;

            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer) || localPlayer == null) return flow;

            FlowController mine = localPlayer.GetFlowBehaviour();
            if (mine == null || mine.Pointer != flow.Pointer) return flow;

            if (!_warnedAboutSharedFlow)
            {
                _warnedAboutSharedFlow = true;
                MelonLogger.Error("[Puppet] this body shares the local player's flow controller. Leaving it alone - " +
                    "refusing its skill flow changes would stop the spells of the person playing costing anything.");
            }

            return null;
        }

        /// <summary>
        /// A body's own stamina controller, or null when it does not have one to itself.
        ///
        /// Asked before every write, because a shared controller is not a theory. A puppet is cloned
        /// from the player prefab, and if the clone ends up pointing at the same controller the local
        /// player uses, then everything done here in the belief that it only touches a remote body lands
        /// on the person playing: their bar snaps to whatever the far player last reported, thirty times a
        /// second, and switching infinite stamina on or off decides it for them too.
        ///
        /// This is the mechanism behind the long-standing note that a swinging puppet appeared to drain
        /// the watching player's stamina - a note that survived two attempts to explain it from the call
        /// graph, because the call graph is right and the object identity is what is wrong. Comparing the
        /// two is cheap and settles it per body rather than in the abstract.
        ///
        /// Compared by pointer: these are proxies over native objects, so two proxies for one controller
        /// are different managed instances, and Unity's equality operator answers a question about
        /// destruction rather than about identity.
        /// </summary>
        /// <returns>The controller when it belongs to this body alone; null when it is missing or shared.</returns>
        internal static StaminaController? OwnStaminaOf(Actor? puppet)
        {
            if (puppet == null) return null;

            StaminaController stamina = puppet.GetStaminaController();
            if (stamina == null)
            {
                MelonLogger.Warning("[Puppet] no stamina controller on the body; its guard will never break.");
                return null;
            }

            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer) || localPlayer == null) return stamina;

            StaminaController mine = localPlayer.GetStaminaController();
            if (mine == null || mine.Pointer != stamina.Pointer) return stamina;

            if (!_warnedAboutSharedStamina)
            {
                _warnedAboutSharedStamina = true;
                MelonLogger.Error("[Puppet] this body shares the local player's stamina controller. Leaving it alone - " +
                    "writing it would spend the stamina of the person playing. A remote guard will not break here.");
            }

            return null;
        }

        /// <summary>
        /// Turns the clone's AI off exactly as <c>MorphUtil.SetupNewActor</c> does when the game itself
        /// takes control of an actor. Without it the player prefab's own AI drives the puppet.
        ///
        /// After activation, not before: the modules do not exist until Awake has run.
        /// </summary>
        private static void DeactivateAI(Actor puppet)
        {
            try
            {
                puppet.AIModule.DeactivateAI();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not deactivate AI: " + e.Message);
            }
        }
    }
}
