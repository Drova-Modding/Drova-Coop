using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// The summons of a client's leased fight, on the host too.
    ///
    /// A fight a client runs (an arena champion it booked) conjures in that client's world only: the host's copy of the
    /// caster is moved by the client's snapshots (LeaseMirror) and never casts, so the host saw the champion fight alone.
    /// The client reports each conjure; the host builds the same prefab at the spot, leases it to that client and announces
    /// it as it does its own spawns. The client finds its own summon in that announcement and binds it to the host's
    /// handle instead of building a second - from then on it is one more leased fighter: the client's snapshots move the
    /// host's copy, the host's blows are relayed in. When the lease ends the host's copy goes with it.
    /// </summary>
    internal static class LeaseSummons
    {
        // How near the announced spot has to be to the reported one. The host builds exactly where it was told.
        private const float SameSpot = 1f;

        private static readonly List<(Actor Body, string Prefab, Vector2 At)> _reported = new();
        private static readonly HashSet<EnemyNetId> _own = new();
        private static readonly Dictionary<EnemyNetId, Actor> _copies = new();
        private static readonly List<EnemyNetId> _gone = new();

        /// <summary>Summons of a client's fight built on the host. For tests.</summary>
        internal static int Built { get; private set; }

        /// <summary>This client's summons bound to the host's handle. For tests.</summary>
        internal static int Bound { get; private set; }

        /// <summary>Whether this creature is a summon of this client's leased fight - leased like its caster.</summary>
        internal static bool IsOwn(EnemyNetId id) => _own.Contains(id);

        /// <summary>Client: a creature leased here conjured <paramref name="body"/>. Tells the host.</summary>
        internal static void Report(Actor body, string prefab)
        {
            _reported.RemoveAll(entry => entry.Body == null);

            Vector2 at = body.transform.position;
            _reported.Add((body, prefab, at));
            CoopSession.ReportLeaseSummon(new LeaseSummonMessage { Prefab = prefab, X = at.x, Y = at.y });
        }

        /// <summary>Client: an announced spawn that is one of this client's reported summons is bound to it, not built.</summary>
        internal static bool TryAdopt(in EnemySpawnMessage message)
        {
            // Already adopted: the host repeats its announcements whenever the roster changes.
            if (_own.Contains(message.NetId)) return true;

            for (int index = 0; index < _reported.Count; index++)
            {
                (Actor body, string prefab, Vector2 at) = _reported[index];
                if (body == null || message.AssetGuid != SpawnedEnemies.PrefabRecipe + prefab) continue;
                if ((at - new Vector2(message.X, message.Y)).sqrMagnitude > SameSpot * SameSpot) continue;

                _reported.RemoveAt(index);
                EnemyRegistry.BindSpawnedBody(message.NetId, body);
                _own.Add(message.NetId);
                Bound++;
                return true;
            }

            return false;
        }

        /// <summary>Host: a client's leased fight conjured. Built here, leased to that client, announced to all.</summary>
        internal static void Take(PlayerId sender, in LeaseSummonMessage message)
        {
            try
            {
                // Only from a client with a fight of its own running.
                if (string.IsNullOrEmpty(message.Prefab) || !EnemyEngagements.LeasesTo(sender)) return;
                if (!SpawnedEnemies.TryBuildFromPrefab(SpawnedEnemies.PrefabRecipe + message.Prefab, new Vector2(message.X, message.Y), out Actor body)) return;

                SpawnedEnemies.ShareInstantiated(body, message.Prefab);
                if (!EnemyRegistry.TryGetSessionId(body, out EnemyNetId id))
                {
                    UnityEngine.Object.Destroy(body.gameObject);
                    return;
                }

                EnemyEngagements.Lease(id, sender);
                _copies[id] = body;
                if (Built++ == 0) MelonLogger.Msg("[Lease] a teammate's fight conjured; its summon stands here too, moved by them.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Lease] building a teammate's summon failed: " + e);
            }
        }

        /// <summary>Host: a summon whose lease ended (the client's died or vanished) goes too. Once per frame.</summary>
        internal static void Update()
        {
            if (_copies.Count == 0) return;

            _gone.Clear();
            foreach (KeyValuePair<EnemyNetId, Actor> copy in _copies)
            {
                if (copy.Value == null || !EnemyEngagements.TryGetLease(copy.Key, out _)) _gone.Add(copy.Key);
            }

            foreach (EnemyNetId id in _gone)
            {
                if (_copies[id] != null) ActorSpawnAccess.TryKill(_copies[id]);
                _copies.Remove(id);
            }
        }

        /// <summary>Forgets everything. Called when a world or session ends.</summary>
        internal static void Clear()
        {
            _reported.Clear();
            _own.Clear();
            _copies.Clear();
        }
    }
}
