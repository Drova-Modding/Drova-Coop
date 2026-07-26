#if DEBUG
using Drova_Modding_API.Access;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// M1's puppet spike: can a second player body exist in the world without corrupting anything?
    ///
    /// Two surveyed co-op projects stalled permanently here, so this deliberately answers the cheapest
    /// version of the question first - spawn a correct, inert clone - rather than trying to drive it.
    /// Movement is M4's problem and is not attempted.
    ///
    /// The recipe is the game's own, read out of <c>PlayableSceneSetup.SetupScene</c>: instantiate
    /// <c>_playerPrefab</c> and then, crucially, <em>do not</em> call
    /// <c>IPlayerController.SetPlayerActor</c>. That call is the only thing in the assembly that marks
    /// an actor as the player, so skipping it is what keeps this clone invisible to the game's
    /// CheckIsPlayer tests while still giving it the right skeleton, cloth pipeline and animator.
    ///
    /// Debug-only. Spawning a second player body into a live world is a spike, not a feature -
    /// <b>use a throwaway save.</b>
    /// </summary>
    internal static class PuppetSpike
    {
        private static GameObject? _holder;
        private static Actor? _puppet;

        /// <summary>
        /// Spawns the puppet, or despawns it if one is already out.
        /// </summary>
        internal static void Toggle()
        {
            if (_puppet != null)
            {
                Despawn();
                return;
            }

            try
            {
                Spawn();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Puppet] spawn failed: " + e);
                Despawn();
            }
        }

        private static void Spawn()
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player))
            {
                MelonLogger.Warning("[Puppet] no player yet; load a world first.");
                return;
            }

            Actor? prefab = FindPlayerPrefab();
            if (prefab == null)
            {
                MelonLogger.Error("[Puppet] PlayableSceneSetup._playerPrefab not found; nothing to clone.");
                return;
            }

            // Instantiated under an inactive parent so the clone is inactive in the hierarchy and Unity
            // never runs its Awake. That is the whole trick: the save components have to be gone before
            // they ever start, because ASaveRoot.InitRoots sweeps GetComponentsInChildren and the player
            // save key is the hardcoded constant "Player#" - a second live one reads and writes the real
            // player's save bucket.
            _holder = new GameObject("DrovaCoop.PuppetHolder");
            _holder.SetActive(false);

            _puppet = Object.Instantiate(prefab, _holder.transform);
            _puppet.name = "DrovaCoop.Puppet";

            int stripped = StripSaveables(_puppet.gameObject);

            // Only now is it safe to let the clone start.
            _puppet.transform.SetParent(player.transform.parent, worldPositionStays: false);
            _puppet.transform.position = player.transform.position + new Vector3(2f, 0f, 0f);
            _puppet.gameObject.SetActive(true);

            DeactivateAI(_puppet);

            MelonLogger.Msg($"[Puppet] spawned at {_puppet.transform.position}, {stripped} save components stripped.");
            Report(player, _puppet);
        }

        /// <summary>
        /// Finds the player prefab the game itself spawns. <c>PlayableSceneSetup</c> is a
        /// ScriptableObject, so it is reachable as a loaded asset rather than needing a live scene
        /// object - which is also why <c>FindObjectsOfTypeAll</c> is the right call and its usual
        /// hazard, returning assets as well as instances, is what we want here.
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
        /// Destroys every save component on the clone and reports what went.
        ///
        /// Both hierarchies have to go and they are unrelated: <c>ASaveRoot</c> owns the object's save
        /// bucket, <c>ASaveableMonoBehaviourBase</c> writes into one. Neither derives from the other, so
        /// looking for one and not the other leaves half the problem in place.
        ///
        /// Note the API's <c>NoSaveGuidRegistry</c> cannot help here, whatever the roadmap says: it
        /// excludes by instance key, and the player's key is the constant "Player#" shared with the real
        /// player, so registering it would switch off the real player's saving too.
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

                MelonLogger.Msg($"[Puppet] stripping {component.GetIl2CppType().Name} from {component.gameObject.name}");
                Object.DestroyImmediate(component);
                stripped++;
            }

            return stripped;
        }

        /// <summary>
        /// Turns the clone's AI off exactly as <c>MorphUtil.SetupNewActor</c> does when the game itself
        /// takes control of an actor. Without it the player prefab's own AI drives the puppet.
        /// </summary>
        private static void DeactivateAI(Actor puppet)
        {
            try
            {
                puppet.AIModule.DeactivateAI();
                MelonLogger.Msg("[Puppet] AI deactivated.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not deactivate AI: " + e.Message);
            }
        }

        /// <summary>
        /// Reports what the clone looks like to the game, which is the actual result of the spike.
        /// </summary>
        private static void Report(Actor player, Actor puppet)
        {
            try
            {
                MelonLogger.Msg($"[Puppet] real player IsPlayer={player.IsPlayer}, puppet IsPlayer={puppet.IsPlayer}");
                MelonLogger.Msg("[Puppet] IsPlayer true on the puppet is expected: it is derived from the EntityInfo GUID, " +
                    "which a clone shares. What must stay false is CustomStats.IsPlayer, set only by SetPlayerActor.");

                // Whether it rendered is the actual exit criterion, and the log has to be able to answer
                // it: "spawned" only means the object exists. A clone that is active with enabled
                // renderers but invisible on screen is a different problem - sorting layer, camera, or
                // the chunk it sits in - than one that never activated.
                int renderers = 0;
                int enabled = 0;
                foreach (Renderer renderer in puppet.gameObject.GetComponentsInChildren<Renderer>(true))
                {
                    renderers++;
                    if (renderer.enabled) enabled++;
                }

                float distance = Vector3.Distance(player.transform.position, puppet.transform.position);
                MelonLogger.Msg($"[Puppet] active={puppet.gameObject.activeInHierarchy}, renderers={enabled}/{renderers} enabled, " +
                    $"{distance:0.0} units from the player, scene '{puppet.gameObject.scene.name}'");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] report failed: " + e.Message);
            }
        }

        private static void Despawn()
        {
            if (_puppet != null)
            {
                Object.Destroy(_puppet.gameObject);
                _puppet = null;
            }

            if (_holder != null)
            {
                Object.Destroy(_holder);
                _holder = null;
            }

            MelonLogger.Msg("[Puppet] despawned.");
        }
    }
}
#endif
