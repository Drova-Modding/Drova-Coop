using Drova_Modding_API.Access;
using Il2CppDrova;
using Il2CppDrova.Saveables;
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

            // Reparented out of the holder before activating, so the holder can go and the puppet lives in
            // the scene like anything else.
            created.transform.SetParent(null, worldPositionStays: false);
            created.transform.position = position;
            created.gameObject.SetActive(true);

            Object.Destroy(holder);

            DeactivateAI(created);
            DisableCollision(created);

            MelonLogger.Msg($"[Puppet] created '{name}' at {position}, {stripped} save components stripped.");

            puppet = created;
            return true;
        }

        /// <summary>
        /// Destroys a puppet.
        /// </summary>
        internal static void Destroy(Actor? puppet)
        {
            if (puppet == null) return;

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
