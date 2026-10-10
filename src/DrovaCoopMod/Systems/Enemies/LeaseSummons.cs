using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Mirrors summons of a client's leased fight onto the host, where the caster never casts itself.
    /// The client reports each conjure, the host builds and leases it, and the client binds its own body to the announced handle.
    /// </summary>
    internal sealed class LeaseSummons : ICoopSystem
    {
        // Max distance between the reported and announced spot.
        private const float SameSpot = 1f;

        private static readonly List<(Actor Body, string Prefab, Vector2 At)> Reported = new();
        private static readonly HashSet<EnemyNetId> Own = new();
        private static readonly Dictionary<EnemyNetId, Actor> Copies = new();
        private static readonly List<EnemyNetId> Gone = new();

        /// <summary>Summons of a client's fight built on the host. For tests.</summary>
        internal static int Built { get; private set; }

        /// <summary>This client's summons bound to the host's handle. For tests.</summary>
        internal static int Bound { get; private set; }

        /// <summary>Whether this creature is a summon of this client's leased fight.</summary>
        internal static bool IsOwn(EnemyNetId id) => Own.Contains(id);

        /// <summary>Client: a creature leased here conjured <paramref name="body"/>. Tells the host.</summary>
        internal static void Report(Actor body, string prefab)
        {
            Reported.RemoveAll(entry => entry.Body == null);

            Vector2 at = body.transform.position;
            Reported.Add((body, prefab, at));
            ReportLeaseSummon(new LeaseSummonMessage { Prefab = prefab, X = at.x, Y = at.y });
        }

        /// <summary>Client: an announced spawn that is one of this client's reported summons is bound to it, not built.</summary>
        internal static bool TryAdopt(in EnemySpawnMessage message)
        {
            // The host repeats its announcements whenever the roster changes.
            if (Own.Contains(message.NetId)) return true;

            for (int index = 0; index < Reported.Count; index++)
            {
                (Actor body, string prefab, Vector2 at) = Reported[index];
                if (body == null || message.AssetGuid != SpawnedEnemies.PrefabRecipe + prefab) continue;
                if ((at - new Vector2(message.X, message.Y)).sqrMagnitude > SameSpot * SameSpot) continue;

                Reported.RemoveAt(index);
                EnemyRegistry.BindSpawnedBody(message.NetId, body);
                Own.Add(message.NetId);
                Bound++;
                return true;
            }

            return false;
        }

        /// <summary>Host: a client's leased fight conjured. Built here, leased to that client, announced to all.</summary>
        private static void Take(PlayerId sender, in LeaseSummonMessage message)
        {
            try
            {
                if (string.IsNullOrEmpty(message.Prefab) || !EnemyEngagements.LeasesTo(sender)) return;
                if (!SpawnedEnemies.TryBuildFromPrefab(SpawnedEnemies.PrefabRecipe + message.Prefab, new Vector2(message.X, message.Y), out Actor? body)) return;

                SpawnedEnemies.ShareInstantiated(body, message.Prefab);
                if (!EnemyRegistry.TryGetSessionId(body, out EnemyNetId id))
                {
                    UnityEngine.Object.Destroy(body.gameObject);
                    return;
                }

                EnemyEngagements.Lease(id, sender);
                Copies[id] = body;
                if (Built++ == 0) MelonLogger.Msg("[Lease] a teammate's fight conjured; its summon stands here too, moved by them.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Lease] building a teammate's summon failed: " + e);
            }
        }

        /// <summary>Host: a summon whose lease ended (the client's died or vanished) goes too.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (Copies.Count == 0) return;

            Gone.Clear();
            foreach (KeyValuePair<EnemyNetId, Actor> copy in Copies)
            {
                if (copy.Value == null || !EnemyEngagements.TryGetLease(copy.Key, out _)) Gone.Add(copy.Key);
            }

            foreach (EnemyNetId id in Gone)
            {
                if (Copies[id] != null) ActorSpawnAccess.TryKill(Copies[id]);
                Copies.Remove(id);
            }
        }

        /// <summary>Forgets everything. Called when a world or session ends.</summary>
        private static void Clear()
        {
            Reported.Clear();
            Own.Clear();
            Copies.Clear();
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<LeaseSummonMessage>(MessageIds.LeaseSummon, (peer, m) =>
            {
                if (CoopSession.HostsWorld && CoopSession.TryGetSender(peer, out PlayerId sender)) Take(sender, m);
            });
        }

        /// <summary>A creature conjured in this client's leased fight: to the host.</summary>
        private static void ReportLeaseSummon(in LeaseSummonMessage message)
        {
            if (CoopSession.JoinedWorld) NetworkAccess.SendToAll(message);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Clear();
    }
}
