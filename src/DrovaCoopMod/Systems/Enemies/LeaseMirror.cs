using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Shows a client's leased fight on the host: the client sends its fighters and the host drives its held copies
    /// with <see cref="RemoteEnemy"/>, then relays them to other clients.
    /// </summary>
    internal sealed class LeaseMirror : ICoopSystem
    {
        // The host broadcaster's combat rate.
        private const float SendInterval = 1f / 30f;

        private static readonly List<EnemySnapshotMessage.Record> RecordBuffer = new();
        private static readonly Dictionary<EnemyNetId, RemoteEnemy> Driven = new();
        private static readonly List<EnemyNetId> Gone = new();
        private static float _sinceSent;

        // Newest per client.
        private static readonly Dictionary<PlayerId, EnemySnapshotMessage> IncomingLeaseSnapshots = new();

        /// <summary>Copies the host is driving for a client's fight. For coop_status and tests.</summary>
        internal static int Mirrored => Driven.Count;

        /// <summary>Sends this client's leased fighters, or drives the host's copies of them.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            try
            {
                if (CoopSession.JoinedWorld) Send(unscaledDeltaTime);
                else if (CoopSession.HostsWorld) Drive(unscaledDeltaTime);
                else Clear();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Lease] mirroring a leased fight failed: " + e);
            }
        }

        /// <summary>Hands every mirrored copy back. Called when a world or session ends.</summary>
        private static void Clear()
        {
            foreach (RemoteEnemy driver in Driven.Values) driver.HandBack();
            Driven.Clear();
            RecordBuffer.Clear();
        }

        private static void Send(float unscaledDeltaTime)
        {
            _sinceSent += unscaledDeltaTime;
            if (_sinceSent < SendInterval) return;
            _sinceSent = 0f;

            RecordBuffer.Clear();
            Vector2 origin = default;

            foreach (KeyValuePair<EnemyNetId, Actor> bound in EnemyRegistry.BoundBodies)
            {
                if (RecordBuffer.Count >= EnemySnapshotMessage.MaxRecords) break;
                if (!EngageWatch.IsBrawling(bound.Key) || bound.Value == null || !EnemyRegistry.IsReady(bound.Value)) continue;

                Vector2 position = bound.Value.GetFeetPosition();
                if (RecordBuffer.Count == 0) origin = position;
                RecordBuffer.Add(EnemyBroadcaster.Describe(bound.Key, bound.Value, position, origin));
            }

            if (RecordBuffer.Count == 0) return;

            SendLeaseSnapshot(new EnemySnapshotMessage
            {
                OriginX = origin.x,
                OriginY = origin.y,
                Records = [.. RecordBuffer],
                Count = RecordBuffer.Count,
            });
        }

        private static void Drive(float unscaledDeltaTime)
        {
            while (TryTakeLeaseSnapshot(out PlayerId sender, out EnemySnapshotMessage snapshot))
            {
                if (!snapshot.IsPlausible()) continue;

                Vector2 origin = new(snapshot.OriginX, snapshot.OriginY);
                for (int index = 0; index < snapshot.Count; index++)
                {
                    EnemySnapshotMessage.Record record = snapshot.Records[index];

                    // A client speaks only for its own lease.
                    if (!EnemyEngagements.TryGetLease(record.NetId, out PlayerId lessee) || lessee.Value != sender.Value) continue;
                    if (!EnemyRegistry.TryResolveOnHost(record.NetId, out Actor? body) || !EnemyRegistry.IsReady(body)) continue;
                    if (!body.gameObject.activeInHierarchy) continue;

                    if (!Driven.TryGetValue(record.NetId, out RemoteEnemy driver) || driver.Body == null || driver.Body.Pointer != body.Pointer)
                    {
                        driver = new RemoteEnemy(body);
                        Driven[record.NetId] = driver;
                    }

                    driver.Apply(record, origin);
                }
            }

            // A lease that ended gives the copy back to its own AI.
            Gone.Clear();
            foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in Driven)
            {
                if (driven.Value.Body == null || !EnemyEngagements.TryGetLease(driven.Key, out _)) Gone.Add(driven.Key);
                else driven.Value.Tick(unscaledDeltaTime);
            }

            foreach (EnemyNetId id in Gone)
            {
                Driven[id].HandBack();
                Driven.Remove(id);
            }
        }

        void ICoopSystem.EndSession()
        {
            IncomingLeaseSnapshots.Clear();
        }

        /// <summary>Sends the host this client's leased brawl fighters, so its held copies follow.</summary>
        private static void SendLeaseSnapshot(in EnemySnapshotMessage message)
        {
            if (!CoopSession.JoinedWorld) return;

            NetworkAccess.SendToAll(message, Delivery.Sequenced);
        }

        /// <summary>Keeps a client's newest batch about its leased fighters. Host only.</summary>
        internal static void Park(PlayerId sender, in EnemySnapshotMessage message) => IncomingLeaseSnapshots[sender] = message;

        /// <summary>Takes the newest batch a client sent about its leased fighters, and who sent it.</summary>
        private static bool TryTakeLeaseSnapshot(out PlayerId sender, out EnemySnapshotMessage message)
        {
            foreach (KeyValuePair<PlayerId, EnemySnapshotMessage> pending in IncomingLeaseSnapshots)
            {
                sender = pending.Key;
                message = pending.Value;
                IncomingLeaseSnapshots.Remove(sender);
                return true;
            }

            sender = default;
            message = default;
            return false;
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Clear();
        }
    }
}
