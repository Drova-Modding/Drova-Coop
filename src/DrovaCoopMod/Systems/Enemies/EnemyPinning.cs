using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Keeps the creatures a remote player is standing among loaded on this machine, since Drova unloads
    /// bodies by distance from the local player. Every switch set here is reversed on release.
    /// </summary>
    internal sealed class EnemyPinning : ICoopSystem
    {
        /// <summary>How far from a remote player a creature is built. Must match the registry's claim range.</summary>
        private const float ReachRange = 600f;

        /// <summary>Let go of slightly further out, so a player on the boundary does not thrash the loader.</summary>
        private const float ReleaseRange = 700f;

        private const float SweepSeconds = 0.5f;

        /// <summary>How many bodies are held for remote players at once. Past this the host pays frames for a region it cannot see.</summary>
        private const int MaxHeldForRemotes = 48;

        private static readonly HashSet<EnemyNetId> PinnedIds = new();
        private static readonly List<EnemyNetId> Stale = new();

        // Handles held loaded only because a remote player is near, so ours to undo.
        private static readonly Dictionary<string, LazyActor> HeldForRemotesByGuid = new(StringComparer.Ordinal);
        private static readonly List<string> Released = new();

        private static float _sinceSweep;
        private static bool _reportedFull;

        /// <summary>How many creatures the host is holding loaded. For <c>coop_status</c>.</summary>
        internal static int Pinned => PinnedIds.Count;

        /// <summary>How many bodies are held only because a remote player is near them. For <c>coop_status</c>.</summary>
        internal static int HeldForRemotes => HeldForRemotesByGuid.Count;

        /// <summary>
        /// Builds what a remote player is standing among, pins what the host owns, and lets go of the rest.
        /// Reaching runs on both ends, since a bind must resolve on the client too.
        /// </summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
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

            if (!CoopSession.HostsWorld) return;

            try
            {
                foreach (EnemyNetId id in EnemyRegistry.ClaimedIds)
                {
                    if (PinnedIds.Contains(id)) continue;

                    if (Pin(id))
                    {
                        PinnedIds.Add(id);
                    }
                }

                Stale.Clear();

                foreach (EnemyNetId id in PinnedIds)
                {
                    if (!EnemyRegistry.ClaimedIds.Contains(id))
                    {
                        Stale.Add(id);
                    }
                }

                foreach (EnemyNetId id in Stale)
                {
                    Unpin(id);
                    PinnedIds.Remove(id);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] pinning creatures around remote players failed: " + e);
            }
        }

        /// <summary>Lets go of everything. Called when the world goes away or the session ends.</summary>
        private static void Clear()
        {
            foreach (EnemyNetId id in PinnedIds)
            {
                Unpin(id);
            }

            PinnedIds.Clear();

            foreach (KeyValuePair<string, LazyActor> held in HeldForRemotesByGuid)
            {
                Let(held.Value, false);
            }

            HeldForRemotesByGuid.Clear();
            _sinceSweep = 0f;
            _reportedFull = false;
        }

        /// <summary>
        /// Builds the creatures a remote player is standing among, so there is something to claim.
        /// Sets the handle's keep-loaded flag rather than calling <c>Load</c>, so streaming stays the game's own.
        /// </summary>
        private static void ReachAroundRemotePlayers()
        {
            if (RemotePlayers.Positions.Count == 0)
            {
                LetGoOfEverything();
                return;
            }

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            Il2CppSystem.Collections.Generic.Dictionary<string, LazyActor> handles = entities._lazyActorGuidDictionary;
            if (handles == null) return;

            foreach (LazyActor lazy in handles.Values)
            {
                if (lazy == null || lazy.IsDestroyed) continue;

                // The local player's surroundings are the streaming system's own.
                Vector2 where = Where(lazy);

                if (IsNearLocalPlayer(where)) continue;

                if (!RemotePlayers.IsAnyWithin(where, ReachRange)) continue;

                if (lazy.IsDead()) continue;

                // Read only for the few in reach, since each read converts a string.
                string guid = lazy.GUID;
                if (HeldForRemotesByGuid.ContainsKey(guid)) continue;

                if (HeldForRemotesByGuid.Count >= MaxHeldForRemotes)
                {
                    if (!_reportedFull)
                    {
                        _reportedFull = true;

                        MelonLogger.Warning($"[Enemy] already holding {MaxHeldForRemotes} creatures loaded for " +
                            "remote players and will not hold more; anything further from them stays " +
                            "unshared. This many usually means the two players are very far apart.");
                    }

                    // Break, not return, so the release sweep below can bring the count down.
                    break;
                }

                Let(lazy, true);
                HeldForRemotesByGuid[guid] = lazy;
            }

            LetGoOfThoseOutOfReach();
        }

        private static void LetGoOfThoseOutOfReach()
        {
            Released.Clear();

            foreach (KeyValuePair<string, LazyActor> held in HeldForRemotesByGuid)
            {
                LazyActor lazy = held.Value;

                if (lazy == null || lazy.IsDestroyed)
                {
                    Released.Add(held.Key);
                    continue;
                }

                if (RemotePlayers.IsAnyWithin(Where(lazy), ReleaseRange)) continue;

                Let(lazy, false);
                Released.Add(held.Key);
            }

            foreach (string guid in Released)
            {
                HeldForRemotesByGuid.Remove(guid);
            }
        }

        private static void LetGoOfEverything()
        {
            if (HeldForRemotesByGuid.Count == 0) return;

            foreach (KeyValuePair<string, LazyActor> held in HeldForRemotesByGuid)
            {
                Let(held.Value, false);
            }

            HeldForRemotesByGuid.Clear();
        }

        /// <summary>Turns a handle's keep-loaded flag on or off. Written directly because the setter is not public.</summary>
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

        /// <summary>Where a creature's body is, since <c>LazyActor.Position</c> is a marker that never moves.</summary>
        private static Vector2 Where(LazyActor lazy)
        {
            Actor? body = lazy._runtimeActor;

            return body != null ? body.transform.position : lazy.Position;
        }

        private static bool IsNearLocalPlayer(Vector2 position)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor? local)) return false;

            Vector2 host = local.transform.position;

            return (host - position).sqrMagnitude <= ReachRange * ReachRange;
        }

        private static bool Pin(EnemyNetId id)
        {
            if (!EnemyRegistry.TryResolveOnHost(id, out Actor? actor)) return false;

            SetHeld(actor, true);
            return true;
        }

        private static void Unpin(EnemyNetId id)
        {
            if (!EnemyRegistry.TryResolveOnHost(id, out Actor? actor)) return;

            SetHeld(actor, false);
        }

        /// <summary>Turns the game's three unload paths on or off for one creature.</summary>
        private static void SetHeld(Actor actor, bool held)
        {
            try
            {
                actor.AIModule?.GetIAIFactory()?.SetStayActive(held);

                if (actor.CustomStats != null)
                {
                    actor.CustomStats.DisableUnloadingLazy = held;
                }

                LazyActor? lazy = FindHandle(actor);
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
        /// Keeps <c>AI_TargetBhvr.ManagedUpdate</c>, gated on renderer visibility, re-choosing targets for a creature off the host's screen.
        /// Clearing the flag makes <c>IsVisible</c> true, and release restores what <c>Awake</c> computed.
        /// </summary>
        private static void KeepChoosingATarget(Actor actor, bool held)
        {
            actor._hasVisibleCallback = !held && actor._visibleCallback != null;
        }

        private static LazyActor? FindHandle(Actor actor)
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return null;

            string guid = EnemyRegistry.GuidOf(actor);
            if (guid.Length == 0) return null;

            return entities.GetLazyActorFromGuid(guid);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Clear();
        }
    }
}
