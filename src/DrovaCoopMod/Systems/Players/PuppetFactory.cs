using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API.Access;
using DrovaCoopMod.Systems.GameArena;
using Il2CppAssets.Scripts.Entity.Modules;
using Il2CppDrova;
using Il2CppDrova.Alignment;
using Il2CppDrova.Cognition;
using Il2CppDrova.FlowSystem;
using Il2CppDrova.HitSystem;
using Il2CppDrova.Saveables;
using Il2CppDrova.StaminaSystem;
using Il2CppDrova.Utilities.LazyLoading;
using Il2CppInterop.Runtime;
using Il2CppOpenWorldSystem;
using MelonLoader;
using Object = UnityEngine.Object;
using UnityEngine;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Builds a remote player's body: a clone of the player prefab stripped of everything the save system would treat as a second player.
    /// </summary>
    internal static class PuppetFactory
    {
        // Warned once per session, since the condition holds for every puppet.
        private static bool _warnedAboutSharedStamina;
        private static bool _warnedAboutSharedFlow;

        /// <summary>
        /// Clones the player prefab, or the creature a teammate is morphed into, into an inert body at <paramref name="position"/>.
        /// </summary>
        internal static bool TryCreate(string name, Vector3 position, [NotNullWhen(true)] out Actor? puppet, Actor? creature = null)
        {
            puppet = null;

            Actor? prefab = creature != null ? creature : FindPlayerPrefab();
            if (prefab == null)
            {
                MelonLogger.Error("[Puppet] PlayableSceneSetup._playerPrefab not found; cannot create a remote player body.");
                return false;
            }

            // An inactive parent keeps Awake from running, so the save components are gone before they start.
            // The player save key is the constant "Player#", so a second live one writes the real player's save.
            GameObject holder = new("DrovaCoop.PuppetHolder");
            holder.SetActive(false);

            Actor created = Object.Instantiate(prefab, holder.transform);
            created.gameObject.name = name;

            int stripped = StripSaveables(created.gameObject);
            int aggro = StripAggroOnDamage(created.gameObject);

            // While inactive: the reference point registers with the streaming root in OnEnable.
            string streaming = EnsureStreaming(created.gameObject);

            // A fresh EntityInfo guid, since brawls, arenas and knockout observers identify the player by it.
            if (created._entityInfo != null)
            {
                EntityInfo own = Object.Instantiate(created._entityInfo);
                own._guid = Guid.NewGuid().ToString("N");
                created._entityInfo = own;
            }

            created.transform.SetParent(null, worldPositionStays: false);
            created.transform.position = position;
            created.gameObject.SetActive(true);

            // Awake derived false from the fresh guid. Restored for player attack timings, damage rules and threat weight.
            created.IsPlayer = true;

            Object.Destroy(holder);

            Constrain(created);

            MelonLogger.Msg($"[Puppet] created '{name}' at {position}, {stripped} save components stripped, "
                + (aggro > 0 ? $"{aggro} aggro-on-damage behavior(s) removed, " : string.Empty)
                + $"{streaming}.");

            puppet = created;
            return true;
        }

        /// <summary>
        /// Makes a body a puppet: no AI, passable, hittable without owning its health. Idempotent, reapplied after
        /// <c>BrawlActor.RemoveKnockout</c> restores AI and collision.
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
        /// Disables the HUD canvases cloned from the player prefab, which draw over the local player's own.
        /// Only the Canvas component, so references to the widgets stay valid.
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

            // Out of an arena fight first: a starter whose body is gone aborts the fight as the player's loss.
            ArenaTeammates.Leave(puppet);
            StopBeingNoticeable(puppet);

            // Disabled now because Destroy is deferred, and the streaming root would walk a dead point in between.
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

        // PlayableSceneSetup is a ScriptableObject, so FindObjectsOfTypeAll reaches it as a loaded asset.
        private static Actor? FindPlayerPrefab()
        {
            foreach (PlayableSceneSetup setup in Resources.FindObjectsOfTypeAll<PlayableSceneSetup>())
            {
                Actor? prefab = setup._playerPrefab;
                if (prefab != null)
                {
                    return prefab;
                }
            }

            return null;
        }

        // HealthBhvr_OnDmgGetAggro adds a permanent EnemyAlignment toward any attacker, and the target filter reads
        // both directions. Removed while inactive so Start never subscribes it.
        private static int StripAggroOnDamage(GameObject root)
        {
            int stripped = 0;

            foreach (HealthBhvr_OnDmgGetAggro behavior in
                root.GetComponentsInChildren<HealthBhvr_OnDmgGetAggro>(true))
            {
                if (behavior == null) continue;

                Object.DestroyImmediate(behavior);
                stripped++;
            }

            return stripped;
        }

        // ASaveRoot and ASaveableMonoBehaviourBase are unrelated hierarchies, so both are matched by base type.
        // NoSaveGuidRegistry cannot be used: the key "Player#" is shared with the real player.
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

        // Gives the body an essential OW_ReferencePoint so chunks stream around it too. Non-essential points are
        // skipped under ChunkPerfMode.Load. Returns a description for the creation log.
        private static string EnsureStreaming(GameObject root)
        {
            try
            {
                OW_ReferencePoint? point = root.GetComponentInChildren<OW_ReferencePoint>(true);
                bool inherited = point != null;

                if (point == null)
                {
                    Component? added = root.AddComponent(Il2CppType.Of<OW_ReferencePoint>());
                    point = added == null ? null : added.TryCast<OW_ReferencePoint>();
                    if (point == null)
                    {
                        return "no reference point could be added, so the world will not stream around this body";
                    }
                }

                point._isEssential = true;

                // Never _autoFindRoot: OnDisable only unregisters from _defaultRoots, so an auto-found root
                // keeps a dead point and throws every frame.
                point._autoFindRoot = false;
                point._defaultRoots = new Il2CppSystem.Collections.Generic.List<OW_Root>();

                OW_Root? currentRoot =OW_Root.CurrentRoot;
                if (currentRoot == null)
                {
                    return "no streaming root to register with, so the world will not stream around this body";
                }

                point._defaultRoots.Add(currentRoot);

                // Otherwise a body whose owner stands still elsewhere would stream nothing.
                point._disableUntilMoved = false;
                point.enabled = true;

                return inherited
                    ? "reference point inherited from the prefab and made essential"
                    : "essential reference point added";
            }
            catch (Exception e)
            {
                // Not fatal: a body in an unloaded world is still worth showing.
                MelonLogger.Warning("[Puppet] could not set up chunk streaming: " + e.Message);
                return "chunk streaming could not be set up";
            }
        }

        /// <summary>
        /// Turns the body colliders into triggers: movement sweeps ignore triggers, while the AI target scan still finds them.
        /// </summary>
        /// <returns>False when the body has no collider module.</returns>
        internal static bool MakePassable(Actor puppet)
        {
            // Kinematic, so physics does not drift the body between placements.
            foreach (Rigidbody2D rigidbody in puppet.gameObject.GetComponentsInChildren<Rigidbody2D>(true))
            {
                if (rigidbody == null) continue;

                rigidbody.bodyType = RigidbodyType2D.Kinematic;
            }

            IEntityColliderModule? colliders = puppet.GetColliderModule();
            if (colliders == null) return false;

            // The unstuck handler can disable the collider, which hides the body from the AI target scan.
            Collider2D? body =colliders.GetCollider();
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
        /// Re-enables hit receivers the camera-distance lazy loader switched off. Silent, since it runs on a timer.
        /// </summary>
        internal static void KeepHittable(Actor puppet)
        {
            try
            {
                foreach (HitReceiveHandler handler in puppet.gameObject.GetComponentsInChildren<HitReceiveHandler>(true))
                {
                    if (handler == null || handler._onlyCheckWithPlayer) continue;

                    // EnableCollider activates the object. The loader also clears the collider and rigidbody flags.
                    handler.EnableCollider();

                    Collider2D? receiver = handler.GetReceiveCollider();
                    if (receiver != null)
                    {
                        receiver.enabled = true;
                    }

                    Rigidbody2D? rigidbody =handler.GetRigidbody();
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
        /// Registers the body with <c>CognitionOctreeManager</c> so behavior trees can notice it. The manager refuses duplicates.
        /// </summary>
        internal static void KeepNoticeable(Actor puppet)
        {
            try
            {
                IActorCognitionModule? cognition = puppet.CognitionModule;
                if (cognition == null) return;

                ICognitionObject? self =cognition.Actor;
                if (self == null) return;

                if (!CognitionOctreeManager.TryGet(out CognitionOctreeManager manager) || manager == null) return;

                manager.AddCognition(self);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not make the body noticeable: " + e.Message);
            }
        }

        // Before destruction, since the manager walks its list every frame.
        private static void StopBeingNoticeable(Actor puppet)
        {
            try
            {
                IActorCognitionModule? cognition = puppet.CognitionModule;
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

        // Live hit receivers plus invincibility: ChangeHealth skips only the write, so damage events still fire
        // with the full figure, and the never-cleared IsDead flag is never set.
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

                KeepHittable(puppet);

                Health? health = puppet.GetHealth();
                if (health == null)
                {
                    MelonLogger.Warning("[Puppet] no health component; the body cannot be hit and cannot report damage.");
                    return;
                }

                health.SetInvinciblity(true);

                // Whether a local swing lands is RemotePlayer's call, since the host can change the rule live.
                MelonLogger.Msg($"[Puppet] {enabled} hit receivers live, health write-protected.");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Puppet] could not make the body hittable: " + e.Message);
            }
        }

        /// <summary>
        /// Makes this body count the local player as an enemy so <c>HitFilter_Alignment</c> accepts friendly fire.
        /// </summary>
        /// <returns>What was done, for the log, or null when nothing needed doing.</returns>
        internal static string? SetHostility(Actor puppet, bool hostile, ref IAlignment? applied)
        {
            if (hostile == (applied != null)) return null;

            IActorAlignmentModule? alignment = puppet.AlignmentModule;
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

            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer))
            {
                // Still loading. The caller retries next frame.
                return null;
            }

            applied = new EnemyAlignment(localPlayer).Cast<IAlignment>();
            alignment.AddDecoratorAlignment(applied);

            return "hostile to the local player so its hit filters accept a swing";
        }

        /// <summary>
        /// Makes the local player count this body as an enemy, which is what their summons ask via <c>SummonAlignment</c>.
        /// </summary>
        /// <returns>What was done, for the log, or null when nothing needed doing.</returns>
        internal static string? SetLocalHostility(Actor puppet, bool hostile, ref (IAlignment Alignment, IActorAlignmentModule Module)? applied)
        {
            if (hostile == (applied != null)) return null;

            // Taken off the module it went onto: the local player may be a morph creature in between.
            if (!hostile)
            {
                applied!.Value.Module.RemoveAlignment(applied.Value.Alignment);
                applied = null;
                return "no longer an enemy of the local player's summons";
            }

            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer) || localPlayer.AlignmentModule == null) return null;

            IAlignment alignment = new EnemyAlignment(puppet).Cast<IAlignment>();
            localPlayer.AlignmentModule.AddDecoratorAlignment(alignment);
            applied = (alignment, localPlayer.AlignmentModule);
            return "an enemy of the local player's summons too";
        }

        // Infinite stamina, so spending never raises StaminaZeroEvent. RemotePlayer writes the owner's value silently.
        // Known gap: HasEnoughStamina is then always true, so a puppet's block never breaks here.
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

        // The owner already paid for the cast. Refusing the type, not an infinite pool, keeps flow-threshold auras from firing.
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

        // Null when missing or shared with the local player, see OwnStaminaOf.
        private static FlowController? OwnFlowOf(Actor? puppet)
        {
            if (puppet == null) return null;

            FlowController? flow = puppet.GetFlowBehaviour();
            if (flow == null) return null;

            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer)) return flow;

            FlowController? mine =localPlayer.GetFlowBehaviour();
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
        /// The body's own stamina controller, or null when missing or shared with the local player, since a clone can point at the player's.
        /// </summary>
        internal static StaminaController? OwnStaminaOf(Actor? puppet)
        {
            if (puppet == null) return null;

            StaminaController? stamina = puppet.GetStaminaController();
            if (stamina == null)
            {
                MelonLogger.Warning("[Puppet] no stamina controller on the body; its guard will never break.");
                return null;
            }

            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer)) return stamina;

            StaminaController? mine =localPlayer.GetStaminaController();
            if (mine == null || mine.Pointer != stamina.Pointer) return stamina;

            if (!_warnedAboutSharedStamina)
            {
                _warnedAboutSharedStamina = true;
                MelonLogger.Error("[Puppet] this body shares the local player's stamina controller. Leaving it alone - " +
                    "writing it would spend the stamina of the person playing. A remote guard will not break here.");
            }

            return null;
        }

        // As MorphUtil.SetupNewActor does. Only after activation, since the modules exist after Awake.
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
