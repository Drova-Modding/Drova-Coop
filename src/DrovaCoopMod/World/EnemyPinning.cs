using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps the creatures a remote player is standing among loaded on this machine.
    ///
    /// The host owns their health, so the host has to have them. Drova swaps a creature's body out for
    /// a lightweight placeholder once it is far enough from the player, and on the host that distance is
    /// measured from the host's own character. A client fighting at the other end of the map is therefore
    /// fighting creatures the host has already put away - and a creature the host cannot resolve is one it
    /// releases, so the two players' damage stops pooling exactly when they are furthest apart and least
    /// able to work out why.
    ///
    /// Three switches, because the game has three separate reasons to unload a body and turning off one
    /// leaves the other two. All are the game's own, and all are reversed on release - pinning without
    /// unpinning is a host whose memory and frame time grow for as long as the session lasts, which is
    /// the failure this file has to avoid more than any other.
    /// </summary>
    internal static class EnemyPinning
    {
        /// <summary>
        /// How far from a remote player a creature is built.
        ///
        /// The same number the registry claims at, and it has to be: a body held any closer than the claim
        /// range would be built and immediately released again, and one held further would be a body nobody
        /// can see kept alive on the host's frame budget.
        /// </summary>
        private const float ReachRange = 600f;

        /// <summary>Let go of slightly further out, so a player on the boundary does not thrash the loader.</summary>
        private const float ReleaseRange = 700f;

        /// <summary>How often the handles around remote players are swept.</summary>
        private const float SweepSeconds = 0.5f;

        /// <summary>
        /// How many bodies are held for remote players at once.
        ///
        /// Each is an actor the host builds and ticks for somebody else's benefit. A camp is a dozen; this
        /// is the point past which the host is paying for a whole region it cannot see, which costs frames
        /// rather than correctness.
        /// </summary>
        private const int MaxHeldForRemotes = 48;

        private static readonly HashSet<EnemyNetId> _pinned = new();
        private static readonly List<EnemyNetId> _stale = new();

        // Handles held loaded purely because a remote player is standing near them, and therefore whose
        // holding is this mod's to undo. Keyed by guid, which for an authored creature is stable.
        private static readonly Dictionary<string, LazyActor> _heldForRemotes = new(StringComparer.Ordinal);
        private static readonly List<string> _released = new();
        private static readonly List<Vector2> _remotePositions = new();

        private static float _sinceSweep;
        private static bool _reportedFull;

        /// <summary>How many creatures the host is holding loaded. For <c>coop_status</c>.</summary>
        internal static int Pinned => _pinned.Count;

        /// <summary>
        /// How many bodies the host is building purely because a remote player is near them. For
        /// <c>coop_status</c>. Zero while the players are together, and zero forever means the host is not
        /// reaching creatures its friend is standing in the middle of.
        /// </summary>
        internal static int HeldForRemotes => _heldForRemotes.Count;

        /// <summary>
        /// Builds what a remote player is standing among, pins what the host owns, and lets go of the rest.
        /// Call once per frame, after the registry has decided what it owns.
        ///
        /// The reaching runs on both ends and the pinning only on the host. Claiming is the host's alone,
        /// but having a body is not: a creature the host claims and drives beside itself is described to a
        /// client that has never built it, and a bind naming a guid the far side cannot resolve applies to
        /// nothing. So the client reaches around the host's body for exactly the reason the host reaches
        /// around the client's.
        /// </summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            try
            {
                _sinceSweep += unscaledDeltaTime;

                if (_sinceSweep >= SweepSeconds)
                {
                    _sinceSweep = 0f;

                    ReachAroundRemotePlayers();
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] building the creatures around a remote player failed: " + e);
            }

            if (CoopSession.State != CoopSessionState.Hosting) return;

            try
            {
                foreach (EnemyNetId id in CoopSession.ClaimedEnemies)
                {
                    if (_pinned.Contains(id)) continue;

                    if (Pin(id))
                    {
                        _pinned.Add(id);
                    }
                }

                _stale.Clear();

                foreach (EnemyNetId id in _pinned)
                {
                    if (!CoopSession.ClaimedEnemies.Contains(id))
                    {
                        _stale.Add(id);
                    }
                }

                foreach (EnemyNetId id in _stale)
                {
                    Unpin(id);
                    _pinned.Remove(id);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] pinning creatures around remote players failed: " + e);
            }
        }

        /// <summary>
        /// Lets go of everything. Called when the world goes away or the session ends.
        /// </summary>
        internal static void Clear()
        {
            foreach (EnemyNetId id in _pinned)
            {
                Unpin(id);
            }

            _pinned.Clear();

            foreach (KeyValuePair<string, LazyActor> held in _heldForRemotes)
            {
                Let(held.Value, false);
            }

            _heldForRemotes.Clear();
            _sinceSweep = 0f;

            // So the next session says it again if it fills up too. The warning describes a condition, not
            // an event, and a condition that has gone away and come back is worth hearing about twice.
            _reportedFull = false;
        }

        /// <summary>
        /// Builds the creatures a remote player is standing among, so there is something for the host to
        /// claim.
        ///
        /// The missing half of pinning, and the reason it is missing is a circle. Holding keeps a
        /// creature the host already owns from being put away; it cannot reach one the host never had. Drova
        /// decides what to build from the local player's position alone, the registry can only claim bodies
        /// that exist, and nothing builds a body only somebody else can see - so a client fighting at the
        /// other end of the map is fighting creatures the host has no opinion about at all. They are never
        /// claimed, never bound, and never sent, and the failure is silent on both machines: the host is
        /// simply not there, and the client sees whatever its own save decided, which for a camp it cleared
        /// before joining is a field of corpses that nothing will ever stand back up.
        ///
        /// Written as the handle's own keep-loaded flag rather than as a <c>Load</c> call, because that is
        /// the switch the streaming system reads when it decides what to build and what to drop - so the
        /// building, the timing and the eventual unloading stay the game's own rather than this mod's.
        /// </summary>
        private static void ReachAroundRemotePlayers()
        {
            CollectRemotePositions();

            if (_remotePositions.Count == 0)
            {
                LetGoOfEverything();
                return;
            }

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            Il2CppSystem.Collections.Generic.Dictionary<string, LazyActor> handles = entities._lazyActorGuidDictionary;
            if (handles == null) return;

            foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, LazyActor> pair in handles)
            {
                LazyActor lazy = pair.Value;
                if (lazy == null || lazy.IsDestroyed) continue;

                if (_heldForRemotes.ContainsKey(pair.Key)) continue;

                if (_heldForRemotes.Count >= MaxHeldForRemotes)
                {
                    if (!_reportedFull)
                    {
                        _reportedFull = true;

                        MelonLogger.Warning($"[Enemy] already holding {MaxHeldForRemotes} creatures loaded for " +
                            "remote players and will not hold more; anything further from them stays " +
                            "unshared. This many usually means the two players are very far apart.");
                    }

                    // Stops taking on more, rather than leaving the method - the release sweep below is
                    // what brings the count back down, and returning past it made the cap permanent: the
                    // dictionary could never shrink, so the first time two players walked far enough apart
                    // the host stopped reaching for anything at all for the rest of the session.
                    break;
                }

                // This machine's own surroundings are the streaming system's business and are already
                // built. Reaching for them here would take responsibility for unloading creatures this mod
                // never caused to be loaded.
                Vector2 where = Where(lazy);

                if (IsNearLocalPlayer(where)) continue;

                if (!IsNearAnyRemote(where, ReachRange)) continue;

                // A creature the host's own save has dead has no body to build and nothing to share. The
                // client's disagreement about that is settled by the graveyard, not by loading a corpse.
                if (lazy.IsDead()) continue;

                Let(lazy, true);
                _heldForRemotes[pair.Key] = lazy;
            }

            LetGoOfThoseOutOfReach();
        }

        private static void LetGoOfThoseOutOfReach()
        {
            _released.Clear();

            foreach (KeyValuePair<string, LazyActor> held in _heldForRemotes)
            {
                LazyActor lazy = held.Value;

                if (lazy == null || lazy.IsDestroyed)
                {
                    _released.Add(held.Key);
                    continue;
                }

                if (IsNearAnyRemote(Where(lazy), ReleaseRange)) continue;

                Let(lazy, false);
                _released.Add(held.Key);
            }

            foreach (string guid in _released)
            {
                _heldForRemotes.Remove(guid);
            }
        }

        private static void LetGoOfEverything()
        {
            if (_heldForRemotes.Count == 0) return;

            foreach (KeyValuePair<string, LazyActor> held in _heldForRemotes)
            {
                Let(held.Value, false);
            }

            _heldForRemotes.Clear();
        }

        /// <summary>
        /// Turns a handle's keep-loaded flag on or off. Written directly because the setter is not public.
        /// </summary>
        private static void Let(LazyActor? lazy, bool keep)
        {
            try
            {
                if (lazy == null || lazy.IsDestroyed) return;

                lazy._keepLoaded = keep;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Enemy] could not {(keep ? "hold" : "let go of")} a creature for a " +
                    "remote player: " + e);
            }
        }

        private static void CollectRemotePositions()
        {
            _remotePositions.Clear();

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                Actor actor = body.Value.Body;

                if (actor != null)
                {
                    _remotePositions.Add(actor.transform.position);
                }
            }
        }

        /// <summary>
        /// Where a creature actually is, rather than where it was authored.
        ///
        /// <c>LazyActor.Position</c> is the handle's own transform - a scene-baked marker that never moves.
        /// The body it spawns does, and a creature that has been pulled across the map is exactly the case
        /// this file exists for: a client aggroes something, walks off with it, and the marker stays behind.
        /// Measured from the marker, the host lets go of a creature its friend is still fighting, which
        /// releases the claim and leaves that fight unshared - the same failure this reaching was written
        /// to prevent, arriving a minute later instead.
        ///
        /// The marker is still the answer while nothing is built, which is the case the reach is deciding
        /// about in the first place.
        /// </summary>
        private static Vector2 Where(LazyActor lazy)
        {
            Actor body = lazy._runtimeActor;

            return body != null ? body.transform.position : lazy.Position;
        }

        private static bool IsNearAnyRemote(Vector2 position, float range)
        {
            float squared = range * range;

            foreach (Vector2 remote in _remotePositions)
            {
                if ((remote - position).sqrMagnitude <= squared) return true;
            }

            return false;
        }

        private static bool IsNearLocalPlayer(Vector2 position)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor local)) return false;

            Vector2 host = local.transform.position;

            return (host - position).sqrMagnitude <= ReachRange * ReachRange;
        }

        private static bool Pin(EnemyNetId id)
        {
            if (!EnemyRegistry.TryResolveOnHost(id, out Actor actor)) return false;

            SetHeld(actor, true);
            return true;
        }

        private static void Unpin(EnemyNetId id)
        {
            if (!EnemyRegistry.TryResolveOnHost(id, out Actor actor)) return;

            SetHeld(actor, false);
        }

        /// <summary>
        /// Turns the three unload paths on or off for one creature, and settles whether it keeps thinking
        /// about who to fight.
        /// </summary>
        private static void SetHeld(Actor actor, bool held)
        {
            try
            {
                // The factory's own switch: what decides whether this actor is a body or a placeholder.
                actor.AIModule?.GetIAIFactory()?.SetStayActive(held);

                // The custom stat the streaming system consults independently of the factory.
                if (actor.CustomStats != null)
                {
                    actor.CustomStats.DisableUnloadingLazy = held;
                }

                // And the handle's own flag, written directly because the setter for it is not public.
                // A plain bool read once when the streaming system decides what to drop.
                LazyActor lazy = FindHandle(actor);
                if (lazy != null)
                {
                    lazy._keepLoaded = held;
                }

                KeepChoosingATarget(actor, held);
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Enemy] could not {(held ? "hold" : "release")} a creature: " + e);
            }
        }

        /// <summary>
        /// Stops a creature settling on one target forever because nobody here can see it.
        ///
        /// <c>AI_TargetBhvr.ManagedUpdate</c> is gated on <c>Actor.IsVisible</c>, which is Unity's renderer
        /// visibility - so it means "on this machine's screen", and on the host the creatures a client is
        /// fighting never are. That <c>Update</c> is the only thing that clears the cached target, so a
        /// creature that chose the host once keeps walking toward the host, past the client next to it.
        /// Every creature prefab checked carries the callback, so this is doing work rather than guarding
        /// a possibility.
        ///
        /// Only that. A creature holding no target is not frozen: a null cache is recomputed on every
        /// read whether it is watched or not, so this cannot be why a fight fails to start - only why one
        /// aimed at the wrong player never moves on.
        ///
        /// Written as a field rather than patched, because the property is a three-line branch on this flag
        /// and is the shape this build folds into its callers. Clearing it makes the property's own first
        /// branch return true, which is what the game answers for any actor authored without the callback.
        /// Restored by recomputing what <c>Awake</c> computed, so a released creature is left as found.
        /// </summary>
        private static void KeepChoosingATarget(Actor actor, bool held)
        {
            actor._hasVisibleCallback = held ? false : actor._visibleCallback != null;
        }

        private static LazyActor? FindHandle(Actor actor)
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return null;

            string guid = actor.GetGuidComponent()?.GuidString ?? string.Empty;
            if (string.IsNullOrEmpty(guid)) return null;

            return entities.GetLazyActorFromGuid(guid);
        }
    }
}
