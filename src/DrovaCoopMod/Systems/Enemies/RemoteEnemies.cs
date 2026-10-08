using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// The creatures this client shows under the host's control. The driven set is reconciled against the registry every frame,
    /// since bodies can unload without notice.
    /// </summary>
    internal sealed class RemoteEnemies : ICoopSystem
    {
        private static readonly Dictionary<EnemyNetId, RemoteEnemy> Driven = new();
        private static readonly List<EnemyNetId> Gone = new();

        private static bool _reportedDesync;

        private static readonly Queue<EnemySnapshotMessage> IncomingSnapshots = new();

        /// <summary>How many creatures are currently driven here. For <c>coop_status</c>.</summary>
        internal static int Count => Driven.Count;

        /// <summary>Updates refused because this world already killed the creature. Non-zero means the saves disagree.</summary>
        internal static int Desynced { get; private set; }

        /// <summary>Whether the host is steering this body here right now. For diagnostics.</summary>
        internal static bool IsSteered(Actor actor)
        {
            foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in Driven)
            {
                if (driven.Value.IsTakenOver && EnemyRegistry.TryGetBody(driven.Key, out Actor? body)
                    && body.Pointer == actor.Pointer) return true;
            }

            return false;
        }

        /// <summary>Applies whatever has arrived and keeps the driven set honest.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Joined)
            {
                Clear();
                return;
            }

            try
            {
                while (TryTakeEnemySnapshot(out EnemySnapshotMessage snapshot))
                {
                    Apply(snapshot);
                }

                DropWhatIsGone();

                foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in Driven)
                {
                    driven.Value.Tick(unscaledDeltaTime);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] driving the host's creatures failed: " + e);
            }
        }

        /// <summary>Hands every creature back to its own AI and forgets them, or they stay inert forever.</summary>
        private static void Clear()
        {
            foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in Driven)
            {
                driven.Value.HandBack();
            }

            Driven.Clear();
            Desynced = 0;
            _reportedDesync = false;
        }

        /// <summary>Whether this machine already killed the creature, usually per its own save file.</summary>
        private static bool IsLocallyDead(Actor body)
        {
            Health health = body.GetHealth();

            return health != null && health.IsDead;
        }

        private static void Apply(in EnemySnapshotMessage snapshot)
        {
            if (!snapshot.IsPlausible()) return;

            Vector2 origin = new(snapshot.OriginX, snapshot.OriginY);

            for (int index = 0; index < snapshot.Count; index++)
            {
                EnemySnapshotMessage.Record record = snapshot.Records[index];

                if (!EnemyRegistry.TryGetBody(record.NetId, out Actor? body)) continue;

                // Still starting its modules. Driving needs movement state that does not exist yet.
                if (!EnemyRegistry.IsReady(body)) continue;

                // Owned but not driven by the host: only health is arbitrated, it keeps its own behavior.
                if (!EnemyRegistry.IsDriven(record.NetId)) continue;

                // A scene move is staged on this body here. A driven body cannot walk, so the host walks it (ReportWalk)
                // and its moving snapshots are that walk.
                if (SceneMoveWatch.IsMoving(body)) continue;

                // This player's own leased fight. The body here is the real one, the host only mirrors it.
                if (EngageWatch.IsBrawling(record.NetId)) continue;
                if (EngageWatch.IsEngaged(record.NetId) && (record.Flags & EnemySnapshotMessage.FlagMoving) == 0) continue;

                // A corpse is never driven, since the saves may disagree on who is dead. EnemyRebirth rebuilds it,
                // this is the net while that is in flight or failed.
                if (IsLocallyDead(body))
                {
                    // A creature dead in both worlds is agreement, and one awaiting rebirth fixes itself. Neither counts.
                    bool known = EnemyRegistry.TryGetBoundGuid(record.NetId, out string? guid);

                    if (known && EnemyRegistry.IsKnownDead(guid)) continue;

                    if (!known || !EnemyRebirth.IsWorkingOn(guid))
                    {
                        Desynced++;

                        if (!_reportedDesync)
                        {
                            _reportedDesync = true;
                            MelonLogger.Warning("[Enemy] the other player's world still has a creature this " +
                                "one has already killed, and it could not be given a new body. It stays a " +
                                "corpse here rather than being driven around - see coop_status.");
                        }
                    }

                    continue;
                }

                // Swapped for a placeholder: not drawn, so not driven. The swing baseline is dropped,
                // since the host's counter keeps running and could lap it during the sleep.
                if (!body.gameObject.activeInHierarchy)
                {
                    if (Driven.TryGetValue(record.NetId, out RemoteEnemy asleep))
                    {
                        asleep.ForgetSwing();
                    }

                    continue;
                }

                if (!Driven.TryGetValue(record.NetId, out RemoteEnemy driver))
                {
                    driver = new RemoteEnemy(body);
                    Driven[record.NetId] = driver;
                }
                else if (driver.Body == null || driver.Body.Pointer != body.Pointer)
                {
                    // The chunk unloaded and streamed back in. The old driver's object no longer exists.
                    driver = new RemoteEnemy(body);
                    Driven[record.NetId] = driver;
                }

                driver.Apply(record, origin);
            }
        }

        /// <summary>Gives back anything the host has released or whose body has gone.</summary>
        private static void DropWhatIsGone()
        {
            Gone.Clear();

            foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in Driven)
            {
                if (driven.Value.Body == null)
                {
                    Gone.Add(driven.Key);
                    continue;
                }

                if (!EnemyRegistry.TryGetBody(driven.Key, out _) || !EnemyRegistry.IsDriven(driven.Key))
                {
                    Gone.Add(driven.Key);
                }
            }

            foreach (EnemyNetId id in Gone)
            {
                if (Driven.TryGetValue(id, out RemoteEnemy driver))
                {
                    driver.HandBack();
                    Driven.Remove(id);
                }
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<EnemySnapshotMessage>(MessageIds.EnemySnapshot, OnEnemySnapshot);
        }

        void ICoopSystem.EndSession()
        {
            IncomingSnapshots.Clear();
        }

        /// <summary>
        /// Sends where the host-driven creatures are. Sequenced, so a late batch is dropped instead of walking
        /// every creature back one interval.
        /// </summary>
        internal static void BroadcastEnemySnapshot(in EnemySnapshotMessage message)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(message, Delivery.Sequenced);
        }

        /// <summary>Takes the next batch of creature positions that has arrived.</summary>
        private static bool TryTakeEnemySnapshot(out EnemySnapshotMessage message)
            => IncomingSnapshots.TryDequeue(out message);

        private static void OnEnemySnapshot(INetPeer peer, EnemySnapshotMessage message)
        {
            // On the host, a client's leased fighters, attributed by connection.
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                if (CoopSession.TryGetSender(peer, out PlayerId sender)) LeaseMirror.Park(sender, message);
                return;
            }

            if (!CoopSession.IsFromHost()) return;

            // Only the newest: each supersedes the last, and a backlog would retrace old positions.
            // Never clear the spawn queue here, since a spawn is said once.
            IncomingSnapshots.Clear();
            IncomingSnapshots.Enqueue(message);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            // Hands every driven creature back to its own AI, or it stays inert forever.
            if (!loaded) Clear();
        }
    }
}
