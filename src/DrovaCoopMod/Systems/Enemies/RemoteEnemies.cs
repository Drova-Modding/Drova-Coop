using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// The creatures this client shows under the host's control.
    /// Bodies can unload without notice, so the driven set is reconciled against the registry every frame.
    /// </summary>
    internal sealed class RemoteEnemies : ICoopSystem
    {
        /// <summary>Distance from the host's creature past which a sleeping body here is moved to it.</summary>
        private const float SleepJump = 1600f;

        private static readonly Dictionary<EnemyNetId, RemoteEnemy> Driven = new();
        private static readonly List<EnemyNetId> Gone = new();

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // Every attack aimed at the creature's target asks here, including mortar impacts, jumps and spawns at its feet.
            Hooking.TryPostfix(harmony, typeof(Il2CppDrova.AI.Modules.AI_TargetBhvr), nameof(Il2CppDrova.AI.Modules.AI_TargetBhvr.GetCurrentTargetActor),
                typeof(RemoteEnemiesPatches), nameof(RemoteEnemiesPatches.TargetPostfix));
            Hooking.TryPostfix(harmony, typeof(Il2CppDrova.AI.Modules.AI_EntityTargetBhvr), nameof(Il2CppDrova.AI.Modules.AI_EntityTargetBhvr.GetCurrentTargetActor),
                typeof(RemoteEnemiesPatches), nameof(RemoteEnemiesPatches.EntityTargetPostfix));
        }

        private static bool _reportedDesync;

        /// <summary>A stall's backlog past this is dropped rather than replayed.</summary>
        private const int MaxPending = EnemySnapshotMessage.MaxRecords * 8;

        /// <summary>
        /// Every record since the last frame, in arrival order. A batch carries only what changed, so the newest batch alone
        /// would miss the swing a standing creature began in the one before it.
        /// </summary>
        private static readonly List<(EnemySnapshotMessage.Record Record, Vector2 Origin)> Incoming = new();

        /// <summary>Creatures currently driven here.</summary>
        internal static int Count => Driven.Count;

        /// <summary>Updates refused because this world already killed the creature. Non-zero means the saves disagree.</summary>
        internal static int Desynced { get; private set; }

        /// <summary>Whether the host is steering this body right now.</summary>
        internal static bool IsSteered(Actor actor)
        {
            foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in Driven)
            {
                if (driven.Value.IsTakenOver && EnemyRegistry.TryGetBody(driven.Key, out Actor? body)
                    && body.Pointer == actor.Pointer) return true;
            }

            return false;
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Joined)
            {
                Clear();
                return;
            }

            try
            {
                foreach ((EnemySnapshotMessage.Record record, Vector2 origin) in Incoming)
                {
                    Apply(record, origin);
                }

                Incoming.Clear();

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

        private static bool IsLocallyDead(Actor body)
        {
            Health health = body.GetHealth();

            return health != null && health.IsDead;
        }

        private static void Apply(in EnemySnapshotMessage.Record record, Vector2 origin)
        {
            if (!EnemyRegistry.TryGetBody(record.NetId, out Actor? body)) return;

            // Still starting its modules. Driving needs movement state that does not exist yet.
            if (!EnemyRegistry.IsReady(body)) return;

            if (FighterDrive.IsMine(record.NetId))
            {
                FighterDrive.TakeStun(record, body);
                return;
            }

            // Owned but not driven by the host. Only its health is arbitrated.
            if (!EnemyRegistry.IsDriven(record.NetId)) return;

            // A scene move is staged on this body. A driven body cannot walk, so the host walks it.
            if (SceneMoveWatch.IsMoving(body)) return;

            // This player's own leased fight. The body here is the real one, the host only mirrors it.
            if (EngageWatch.IsBrawling(record.NetId)) return;
            // Talking to it here. Only walks this player's own scene handed the host get through.
            // Anything else waits for the dialogue to end, then the next snapshot moves it.
            if (EngageWatch.IsEngaged(record.NetId)
                && ((record.Flags & EnemySnapshotMessage.FlagMoving) == 0 || !CutscenePorts.WalksHere(body))) return;

            // A corpse is never driven, since the saves may disagree on who is dead.
            // EnemyRebirth rebuilds it. This is the fallback while that is pending or failed.
            if (IsLocallyDead(body))
            {
                // A creature dead in both worlds is agreement, and one awaiting rebirth fixes itself. Neither counts.
                bool known = EnemyRegistry.TryGetBoundGuid(record.NetId, out string? guid);

                if (known && EnemyRegistry.IsKnownDead(guid)) return;

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

                return;
            }

            // A placeholder is not driven. Its swing baseline is dropped since the host's counter can lap it.
            // A body left far behind is moved so it wakes where the host's is.
            if (!body.gameObject.activeInHierarchy)
            {
                if (Driven.TryGetValue(record.NetId, out RemoteEnemy asleep))
                {
                    asleep.ForgetSwing();
                }

                Vector2 reported = origin + new Vector2(record.OffsetX * EnemySnapshotMessage.PositionStep, record.OffsetY * EnemySnapshotMessage.PositionStep);
                if ((reported - body.GetFeetPosition()).sqrMagnitude > SleepJump * SleepJump)
                {
                    body.TeleportTo(reported, portWithGroup: false, updateCam: false);
                }

                return;
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

                    // Its AI comes back reset and would have to notice this player all over again.
                    if (driver.Body != null && FighterDrive.IsMine(id)) FighterDrive.Engage(driver.Body);
                }
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<EnemySnapshotMessage>(MessageIds.EnemySnapshot, OnEnemySnapshot);
        }

        void ICoopSystem.EndSession()
        {
            Incoming.Clear();
        }

        /// <summary>
        /// Sends where the host-driven creatures are.
        /// Sequenced, so a late batch is dropped instead of walking every creature back.
        /// </summary>
        internal static void BroadcastEnemySnapshot(in EnemySnapshotMessage message)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(message, Delivery.Sequenced);
        }

        private static void OnEnemySnapshot(INetPeer peer, EnemySnapshotMessage message)
        {
            // On the host, a client's leased fighters, attributed by connection.
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                if (CoopSession.TryGetSender(peer, out PlayerId sender)) LeaseMirror.Park(sender, message);
                return;
            }

            if (!CoopSession.IsFromHost()) return;

            if (!message.IsPlausible()) return;
            if (Incoming.Count > MaxPending) Incoming.Clear();

            Vector2 origin = new(message.OriginX, message.OriginY);
            for (int index = 0; index < message.Count; index++) Incoming.Add((message.Records[index], origin));
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Clear();
        }
    }
}
