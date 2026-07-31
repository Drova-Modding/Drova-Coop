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
    /// Keeps the creatures a remote player is standing among loaded on the host.
    ///
    /// **The host owns their health, so the host has to have them.** Drova swaps a creature's body out for
    /// a lightweight placeholder once it is far enough from the player, and on the host that distance is
    /// measured from the host's own character. A client fighting at the other end of the map is therefore
    /// fighting creatures the host has already put away - and a creature the host cannot resolve is one it
    /// releases, so the two players' damage stops pooling exactly when they are furthest apart and least
    /// able to work out why.
    ///
    /// Three switches, because the game has three separate reasons to unload a body and turning off one
    /// leaves the other two. All are the game's own, and all are reversed on release - **pinning without
    /// unpinning is a host whose memory and frame time grow for as long as the session lasts**, which is
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
        /// Pins what the host owns and lets go of the rest. Call once per frame on the host, after the
        /// registry has decided what it owns.
        /// </summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting)
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
        }

        // ------------------------------------------------------------------ reaching for a friend

        /// <summary>
        /// Builds the creatures a remote player is standing among, so there is something for the host to
        /// claim.
        ///
        /// **The missing half of pinning, and the reason it is missing is a circle.** Holding keeps a
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

                    return;
                }

                // The host's own surroundings are the streaming system's business and are already built.
                // Reaching for them here would take responsibility for unloading creatures this mod never
                // caused to be loaded.
                if (IsNearHostPlayer(lazy.Position)) continue;

                if (!IsNearAnyRemote(lazy.Position, ReachRange)) continue;

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

                if (IsNearAnyRemote(lazy.Position, ReleaseRange)) continue;

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

        private static bool IsNearAnyRemote(Vector2 position, float range)
        {
            float squared = range * range;

            foreach (Vector2 remote in _remotePositions)
            {
                if ((remote - position).sqrMagnitude <= squared) return true;
            }

            return false;
        }

        private static bool IsNearHostPlayer(Vector2 position)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor local) || local == null) return false;

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
        /// Turns the three unload paths on or off for one creature.
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
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Enemy] could not {(held ? "hold" : "release")} a creature: " + e);
            }
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
