using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps a body in the world for every other player in the session, and moves it.
    ///
    /// The roster is the authority on who exists and the state stream is the authority on where they are;
    /// this reconciles both against what is actually in the scene. Reconciling every frame rather than
    /// reacting to join and leave events is deliberate - it is a handful of dictionary lookups, and it
    /// cannot drift out of step with the roster the way a missed event would.
    /// </summary>
    internal static class RemotePlayers
    {
        private static readonly Dictionary<PlayerId, RemotePlayer> _bodies = new();
        private static readonly List<PlayerId> _departed = new();

        // Rebuilt at most once a frame, because the patches that ask are on the game's own hot paths -
        // the AI load gate alone is consulted for thirty actors a frame - and where a body is cannot
        // change between two questions asked in the same frame.
        private static readonly List<Vector2> _positions = new();
        private static int _positionsFrame = -1;

        /// <summary>
        /// How many bodies are currently in the world.
        /// </summary>
        internal static int Count => _bodies.Count;

        /// <summary>
        /// The bodies in the world, by the player they belong to. For anything that has to draw them.
        /// </summary>
        internal static IReadOnlyDictionary<PlayerId, RemotePlayer> Bodies => _bodies;

        /// <summary>
        /// Where every remote player is standing, in the coordinates the game's own distance tests use.
        ///
        /// Feet rather than transform position, so that a comparison against one of the game's own
        /// thresholds means the same thing this side of the patch as the other.
        /// </summary>
        internal static IReadOnlyList<Vector2> Positions
        {
            get
            {
                if (_positionsFrame == Time.frameCount) return _positions;

                _positionsFrame = Time.frameCount;
                _positions.Clear();

                foreach (KeyValuePair<PlayerId, RemotePlayer> body in _bodies)
                {
                    Actor actor = body.Value.Body;
                    if (actor != null)
                    {
                        _positions.Add(actor.GetFeetPosition());
                    }
                }

                return _positions;
            }
        }

        /// <summary>
        /// Whether any remote player is standing within <paramref name="range"/> of a point.
        ///
        /// The question every patch on one of the game's single-player distance tests asks. It answers on
        /// a count while nobody else is here, which is what keeps those patches free in the ordinary case.
        /// </summary>
        internal static bool IsAnyWithin(Vector2 point, float range)
        {
            if (_bodies.Count == 0) return false;

            IReadOnlyList<Vector2> positions = Positions;
            float squared = range * range;

            for (int index = 0; index < positions.Count; index++)
            {
                if ((positions[index] - point).sqrMagnitude <= squared) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether an entity is one of the bodies this mod puppets for a remote player.
        ///
        /// Asked on the path of every ability hit in the game, so it answers "no" - the overwhelmingly
        /// common case - without touching anything but a count while no session is running.
        /// </summary>
        /// <param name="entity">Any entity, including null.</param>
        /// <returns>True only for a body created by <see cref="PuppetFactory"/> and still tracked.</returns>
        internal static bool IsPuppet(Entity entity) => TryFindOwner(entity, out _);

        /// <summary>
        /// Whether an entity is the person playing at this machine. Null is not.
        /// Compared by pointer, since two proxies over one native actor are different managed objects.
        /// </summary>
        internal static bool IsLocalPlayer(Entity? entity)
        {
            if (entity == null) return false;

            return PlayerAccess.TryGetPlayer(out Actor player) && player.Pointer == entity.Pointer;
        }

        /// <summary>
        /// Whether this body belongs to somebody playing: the person at this machine, or one of the
        /// remote players being shown here.
        /// </summary>
        internal static bool IsPlayersBody(Actor? actor)
        {
            return IsPuppet(actor) || IsLocalPlayer(actor);
        }

        /// <summary>
        /// Which player, if any, a health component belongs to the body of.
        ///
        /// Asked on the game's own damage funnel, which fires for every actor in the scene rather than
        /// only for the handful this mod owns, so the common answer is "none" and it has to be cheap:
        /// a walk over at most three entries and a pointer comparison each.
        ///
        /// Compared by pointer because these are proxies over native objects - two proxies for one actor
        /// are different managed instances, and Unity's equality operator answers a question about
        /// destruction rather than about identity.
        /// </summary>
        internal static bool TryFindOwner(Health health, out PlayerId id)
        {
            id = PlayerId.None;

            if (health == null) return false;

            return TryFindOwner(health.OwnerEntity, out id);
        }

        /// <summary>
        /// Which player an entity is the body of, if it is one of them at all.
        /// </summary>
        /// <param name="entity">Any entity, including null.</param>
        /// <param name="id">The owner, or <see cref="PlayerId.None"/>.</param>
        /// <returns>True only for a body created by <see cref="PuppetFactory"/> and still tracked.</returns>
        internal static bool TryFindOwner(Entity entity, out PlayerId id)
        {
            id = PlayerId.None;

            if (entity == null || _bodies.Count == 0) return false;

            // By pointer, not with the equality operator: these are proxies over native objects, and two
            // proxies for one object are different managed instances.
            IntPtr pointer = entity.Pointer;

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in _bodies)
            {
                if (body.Value.Body == null) continue;
                if (body.Value.Body.Pointer != pointer) continue;

                id = body.Key;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Spawns, despawns and drives remote bodies. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            // A body cannot be built before there is a world to put it in, and PlayerAccess is the
            // readiness signal for that: it reports a player only once the world is up and the actor is
            // initialized.
            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer)) return;

            SyncRoster(localPlayer);
            ApplyStates(unscaledDeltaTime);
        }

        /// <summary>
        /// Removes every body. Called when a session ends, and when the world goes away underneath one.
        /// </summary>
        internal static void Clear()
        {
            if (_bodies.Count == 0) return;

            foreach (RemotePlayer body in _bodies.Values)
            {
                PuppetFactory.Destroy(body.Body);
            }

            _bodies.Clear();
            MelonLogger.Msg("[Puppet] all remote bodies removed.");
        }

        /// <summary>
        /// Adds a body for anyone in the roster who has none, and removes bodies for anyone who left.
        /// </summary>
        private static void SyncRoster(Actor localPlayer)
        {
            DropBodiesThatDidNotSurvive();

            foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
            {
                if (player.Key == CoopSession.LocalPlayerId) continue;
                if (_bodies.ContainsKey(player.Key)) continue;

                // Spawned on top of the local player rather than at the origin: the remote player's real
                // position arrives a fraction of a second later, and a body that appears across the map
                // and slides in is worse than one that appears close and corrects.
                if (!PuppetFactory.TryCreate($"DrovaCoop.Remote.{player.Key.Value}", localPlayer.transform.position, out Actor? body) || body == null)
                {
                    continue;
                }

                RemotePlayer created = new(player.Key, body);

                // A body is born knowing nothing, and the session may already be holding a cast from
                // before it existed. Taking the count without performing the spell is what stops a body
                // appearing mid-cast the instant it is built - which every player who had ever cast would
                // otherwise do, all at once, the moment a reloaded world rebuilt them.
                if (CoopSession.ItemUses.TryGetValue(player.Key, out ItemUseSnapshot pending))
                {
                    created.AdoptItemUse(pending);
                }

                _bodies[player.Key] = created;
                MelonLogger.Msg($"[Puppet] body created for {player.Key} '{player.Value}'.");
            }

            _departed.Clear();
            foreach (PlayerId present in _bodies.Keys)
            {
                if (!CoopSession.Players.ContainsKey(present))
                {
                    _departed.Add(present);
                }
            }

            foreach (PlayerId gone in _departed)
            {
                PuppetFactory.Destroy(_bodies[gone].Body);
                _bodies.Remove(gone);
                MelonLogger.Msg($"[Puppet] body removed for {gone}.");
            }
        }

        /// <summary>
        /// Forgets bodies whose actor no longer exists, so the roster rebuilds them.
        ///
        /// Loading a save is the case this exists for. It tears the world down and builds a new one
        /// without ever passing through the main menu, so the session is still live, this mod is never
        /// told the world went away, and <see cref="Clear"/> - which only runs on the way to the menu -
        /// does not fire. Every puppet is destroyed along with the world all the same. What is left is a
        /// dictionary of keys whose bodies answer to nothing, and because the roster sync only asks
        /// whether a key is present, it would skip every one of them and never show those players again
        /// for the rest of the session.
        ///
        /// Checked by Unity's own equality rather than a reference comparison, because that is the one
        /// case where the operator is the right tool: it answers whether the object has been destroyed,
        /// which is exactly the question here.
        /// </summary>
        private static void DropBodiesThatDidNotSurvive()
        {
            if (_bodies.Count == 0) return;

            _departed.Clear();

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in _bodies)
            {
                if (body.Value.Body == null)
                {
                    _departed.Add(body.Key);
                }
            }

            foreach (PlayerId lost in _departed)
            {
                // Not destroyed, only forgotten: the actor is already gone, and asking the factory to
                // destroy it again would be an operation on a dead object.
                _bodies.Remove(lost);
                MelonLogger.Msg($"[Puppet] the body for {lost} did not survive the world; rebuilding it.");
            }
        }

        /// <summary>
        /// Feeds each body whatever arrived for it and lets the game move it.
        /// </summary>
        private static void ApplyStates(float unscaledDeltaTime)
        {
            foreach (KeyValuePair<PlayerId, RemotePlayer> body in _bodies)
            {
                if (CoopSession.LatestStates.TryGetValue(body.Key, out PlayerStateMessage state))
                {
                    body.Value.Record(state);
                }

                if (CoopSession.Vitals.TryGetValue(body.Key, out PlayerVitalsMessage vitals))
                {
                    body.Value.RecordVitals(vitals);
                }

                // Ahead of the intent, so a swing that arrives in the same frame as the weapon it was made
                // with is performed with that weapon rather than with the last one.
                if (CoopSession.Equipment.TryGetValue(body.Key, out EquipmentSnapshot equipment))
                {
                    body.Value.RecordEquipment(equipment);
                }

                // After the equipment, for the same reason the equipment comes before the intent: some
                // abilities pick their effect from the weapon the caster is holding, so a spell that
                // arrives in the same frame as the weapon is cast with that weapon rather than the last.
                if (CoopSession.ItemUses.TryGetValue(body.Key, out ItemUseSnapshot cast))
                {
                    body.Value.RecordItemUse(cast);
                }

                // Applied every frame, not only on arrival: intent is a standing instruction, and the
                // movement module expects to be told the direction each tick the way the input layer
                // tells it.
                body.Value.Apply(unscaledDeltaTime);
            }
        }
    }
}
