using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// A client's leased fight, shown on the host.
    ///
    /// A scripted brawl a client starts is fought in that client's world (the lease, EnemyEngagements): its NPCs are the
    /// live ones there, and the host's copies stand held. Held is all they did - the Red Tower's sibling duel ran all over
    /// the room on the client while the host watched Jero and Molvina stand where it began, and snap to where it ended.
    /// Now the client sends its fighters the way the host sends its creatures, and the host drives its held copies with
    /// the same driver a client uses for the host's. The host's own batches then carry them on to every other client;
    /// the client fighting ignores those (RemoteEnemies), and the host bills no blow its copy's replayed swings land
    /// (DamageWatch).
    /// </summary>
    internal static class LeaseMirror
    {
        // The host broadcaster's combat beat: a fight is what this sends.
        private const float SendInterval = 1f / 30f;

        private static readonly List<EnemySnapshotMessage.Record> _records = new();
        private static readonly Dictionary<EnemyNetId, RemoteEnemy> _driven = new();
        private static readonly List<EnemyNetId> _gone = new();
        private static float _sinceSent;

        /// <summary>Copies the host is driving for a client's fight. For coop_status and tests.</summary>
        internal static int Mirrored => _driven.Count;

        /// <summary>Sends this client's leased fighters, or drives the host's copies of them. Once per frame.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            try
            {
                if (CoopSession.State == CoopSessionState.Joined) Send(unscaledDeltaTime);
                else if (CoopSession.State == CoopSessionState.Hosting) Drive(unscaledDeltaTime);
                else Clear();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Lease] mirroring a leased fight failed: " + e);
            }
        }

        /// <summary>Hands every mirrored copy back. Called when a world or session ends.</summary>
        internal static void Clear()
        {
            foreach (RemoteEnemy driver in _driven.Values) driver.HandBack();
            _driven.Clear();
            _records.Clear();
        }

        private static void Send(float unscaledDeltaTime)
        {
            _sinceSent += unscaledDeltaTime;
            if (_sinceSent < SendInterval) return;
            _sinceSent = 0f;

            _records.Clear();
            Vector2 origin = default;

            foreach (KeyValuePair<EnemyNetId, Actor> bound in EnemyRegistry.BoundBodies)
            {
                if (_records.Count >= EnemySnapshotMessage.MaxRecords) break;
                if (!EngageWatch.IsBrawling(bound.Key) || bound.Value == null || !EnemyRegistry.IsReady(bound.Value)) continue;

                Vector2 position = bound.Value.GetFeetPosition();
                if (_records.Count == 0) origin = position;
                _records.Add(EnemyBroadcaster.Describe(bound.Key, bound.Value, position, origin));
            }

            if (_records.Count == 0) return;

            CoopSession.SendLeaseSnapshot(new EnemySnapshotMessage
            {
                OriginX = origin.x,
                OriginY = origin.y,
                Records = [.. _records],
                Count = _records.Count,
            });
        }

        private static void Drive(float unscaledDeltaTime)
        {
            while (CoopSession.TryTakeLeaseSnapshot(out PlayerId sender, out EnemySnapshotMessage snapshot))
            {
                if (!snapshot.IsPlausible()) continue;

                Vector2 origin = new(snapshot.OriginX, snapshot.OriginY);
                for (int index = 0; index < snapshot.Count; index++)
                {
                    EnemySnapshotMessage.Record record = snapshot.Records[index];

                    // Only what is leased to the sender: a client speaks for its own fight and nothing else.
                    if (!EnemyEngagements.TryGetLease(record.NetId, out PlayerId lessee) || lessee.Value != sender.Value) continue;
                    if (!EnemyRegistry.TryResolveOnHost(record.NetId, out Actor body) || !EnemyRegistry.IsReady(body)) continue;
                    if (!body.gameObject.activeInHierarchy) continue;

                    if (!_driven.TryGetValue(record.NetId, out RemoteEnemy? driver) || driver.Body == null || driver.Body.Pointer != body.Pointer)
                    {
                        driver = new RemoteEnemy(body);
                        _driven[record.NetId] = driver;
                    }

                    driver.Apply(record, origin);
                }
            }

            // A lease that ended gives the copy back to its own AI.
            _gone.Clear();
            foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in _driven)
            {
                if (driven.Value.Body == null || !EnemyEngagements.TryGetLease(driven.Key, out _)) _gone.Add(driven.Key);
                else driven.Value.Tick(unscaledDeltaTime);
            }

            foreach (EnemyNetId id in _gone)
            {
                _driven[id].HandBack();
                _driven.Remove(id);
            }
        }
    }
}
