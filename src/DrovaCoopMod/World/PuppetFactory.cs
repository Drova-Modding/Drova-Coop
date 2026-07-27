using Drova_Modding_API.Access;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using Il2CppInterop.Runtime;
using Il2CppDrova.StaminaSystem;
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

            DeactivateAI(created);
            DisableCollision(created);
            StopPayingForActions(created);

            MelonLogger.Msg($"[Puppet] created '{name}' at {position}, {stripped} save components stripped, {streaming}.");

            puppet = created;
            return true;
        }

        /// <summary>
        /// Destroys a puppet.
        /// </summary>
        internal static void Destroy(Actor? puppet)
        {
            if (puppet == null) return;

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
        /// The prefab may well bring its own point, since the real player streams the world the same way.
        /// It is reused when it does - a second point on the same body would ask the root to union a radius
        /// with itself - and it is reconfigured either way, because the flag it was authored with is the one
        /// that suits the player, and nothing guarantees that is this one.
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

                // **Never `_autoFindRoot`, however much it looks like the field for this.** The point's
                // OnEnable registers with every root in `_defaultRoots` *and*, if that flag is set, with
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
        /// Takes the body out of the physics world.
        ///
        /// A remote body is placed where its owner says it is, and that placement does not consult
        /// collision - it cannot, because the owner has already decided where they are. A solid body moved
        /// that way is a moving wall: it pushes whatever it lands on, and what it usually lands on is the
        /// local player, who ends up shoved through geometry into places they cannot walk back out of.
        ///
        /// Nothing is lost by removing it today. The body cannot be hit and does not simulate, so its
        /// colliders do no work; they only produce that one effect. Collision has to come back when combat
        /// needs the body to be a target, and by then it will need to be driven by something that can be
        /// stopped by a wall.
        /// </summary>
        private static void DisableCollision(Actor puppet)
        {
            int disabled = 0;

            foreach (Collider2D collider in puppet.gameObject.GetComponentsInChildren<Collider2D>(true))
            {
                if (collider == null) continue;

                collider.enabled = false;
                disabled++;
            }

            // Kinematic as well as collider-free: a dynamic body still integrates gravity and velocity
            // between the frames that place it, which fights the placement and can drift it off a ledge.
            foreach (Rigidbody2D rigidbody in puppet.gameObject.GetComponentsInChildren<Rigidbody2D>(true))
            {
                if (rigidbody == null) continue;

                rigidbody.bodyType = RigidbodyType2D.Kinematic;
            }

            MelonLogger.Msg($"[Puppet] {disabled} colliders disabled; the body cannot push anything.");
        }

        /// <summary>
        /// Stops the body's actions costing anything.
        ///
        /// **A remote body must not run resource economics.** Its owner already paid: the swing happened on
        /// their machine, against their stamina, and what arrives here is a report of it. Charging for it
        /// again on this machine is charging twice for one swing, and it is the far player's own game that
        /// is authoritative for their stamina, exactly as this mod's README says of everything a client
        /// owns about itself.
        ///
        /// It also, in practice, charged the wrong person: a puppet swinging drained the watching player's
        /// stamina. **The decompiled game does not explain that**, which is worth writing down so the
        /// wrong thing is not fixed twice. The cost comes from
        /// <c>AA_Bhvr_StaminaCondition.ActionStartedEventListener</c>, which spends
        /// <c>action.GetActor().GetStaminaController()</c> - the acting actor's own controller - and the
        /// acting actor is stamped onto every action instance by
        /// <c>ActionModule.InstantiateActionInstance</c> from the module's own actor. There is no global
        /// on that path at all. So either the drain was the on-screen bar rather than the stamina, or it
        /// arrives by a route not yet found; the Debug report in <c>RemotePlayer</c> names which of the
        /// two bars moved and settles it.
        ///
        /// Done with the game's own switch rather than by refusing the cost, because the switch is a field
        /// on this body's own controller: it cannot reach the real player's, whatever the stat underneath
        /// turns out to be shared with.
        /// </summary>
        private static void StopPayingForActions(Actor puppet)
        {
            try
            {
                StaminaController stamina = puppet.GetStaminaController();
                if (stamina == null)
                {
                    MelonLogger.Warning("[Puppet] no stamina controller on the body; its actions may charge the local player.");
                    return;
                }

                stamina.SetInfiniteStamina(true);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not make actions free: " + e.Message);
            }
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
