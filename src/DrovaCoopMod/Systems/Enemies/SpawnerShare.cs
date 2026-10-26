using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppDrova;
using Il2CppDrova.HitSystem;
using Il2CppDrova.Spawners;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Lets one machine run each of the game's wave spawners (bloodquito nests, flesh waves). The host shares what its
    /// spawners make through <see cref="SpawnedEnemies"/>. A client runs its own only while the host is not in the
    /// volume, since every machine's spawner otherwise rolls its own waves with fresh guids nobody else can name.
    /// </summary>
    internal sealed class SpawnerShare : ICoopSystem
    {
        private const float PollSeconds = 0.25f;

        // SpawnData.DestroyActorsTooFarAway's reach: past it, the host's wave is gone on the host.
        private const float HostGoneDistance = 600f;

        // Every spawner this machine's player walked into, with the volume that starts it.
        private static readonly Dictionary<IntPtr, (SpawnController Controller, HitReceiveHandler? Volume)> Known = new();
        private static readonly List<IntPtr> Gone = new();

        // Host: the spawned bodies already handed to SpawnedEnemies, rebuilt every poll so a reused pointer is seen anew.
        private static HashSet<IntPtr> _recorded = new();
        private static HashSet<IntPtr> _standing = new();

        // What the game's spawners make (bloodquito nests, flesh waves), by the prefab name a spawned body carries.
        private static readonly Dictionary<string, string> AssetByName = new(StringComparer.Ordinal)
        {
            ["Bloodquito_Kamikaze"] = AddressableAccess.Creatures.Bloodquito_Kamikaze.AssetGUID,
            ["EnemyFlesh_Melee"] = AddressableAccess.Creatures.EnemyFlesh_Melee.AssetGUID,
            ["EnemyFlesh_Ranged"] = AddressableAccess.Creatures.EnemyFlesh_Ranged.AssetGUID,
            ["EnemyFlesh_Tank"] = AddressableAccess.Creatures.EnemyFlesh_Tank.AssetGUID,
            ["EnemyFlesh_Baneling"] = AddressableAccess.Creatures.EnemyFlesh_Baneling.AssetGUID,
            ["EnemyFlesh_Plant"] = AddressableAccess.Creatures.EnemyFlesh_Plant.AssetGUID,
        };

        private static float _sincePoll;

        string? ICoopSystem.HookSwitch => HookSwitches.Spawners;

        /// <summary>Spawned creatures the host handed over for sharing. For tests.</summary>
        internal static int Shared { get; private set; }

        /// <summary>Times this client left a spawner to the host standing in it. For tests.</summary>
        internal static int LeftToHost { get; private set; }

        /// <summary>Times this client started a spawner again after the host left it. For tests.</summary>
        internal static int Resumed { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(HitReceiveBhvr_ActivateSpawner), nameof(HitReceiveBhvr_ActivateSpawner.OnEnter),
                typeof(SpawnerSharePatches), nameof(SpawnerSharePatches.EnterPrefix));
        }

        /// <summary>Remembers the spawner, and keeps a client's from starting while the host stands in its volume.</summary>
        internal static bool EnterPrefix(HitReceiveBhvr_ActivateSpawner __instance)
        {
            if (!CoopSession.SharesWorld) return true;

            try
            {
                SpawnController? controller = __instance._spawnController;
                if (controller == null) return true;

                HitReceiveHandler? volume = __instance.GetComponentInParent<HitReceiveHandler>();
                Known[controller.Pointer] = (controller, volume);

                if (!CoopSession.JoinedWorld || volume == null || !HostInside(volume)) return true;

                LeftToHost++;
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Spawner] deciding who runs a spawner failed: " + e);
                return true;
            }
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
            {
                Forget();
                return;
            }

            _sincePoll += unscaledDeltaTime;
            if (_sincePoll < PollSeconds) return;
            _sincePoll = 0f;

            try
            {
                Gone.Clear();
                _standing.Clear();

                foreach (KeyValuePair<IntPtr, (SpawnController Controller, HitReceiveHandler? Volume)> known in Known)
                {
                    (SpawnController controller, HitReceiveHandler? volume) = known.Value;
                    if (controller == null)
                    {
                        Gone.Add(known.Key);
                        continue;
                    }

                    if (CoopSession.HostsWorld) Share(controller);
                    else Arbitrate(controller, volume);
                }

                foreach (IntPtr pointer in Gone) Known.Remove(pointer);

                (_recorded, _standing) = (_standing, _recorded);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Spawner] sharing spawner creatures failed: " + e);
            }
        }

        /// <summary>Host: hands every new body of this spawner's waves to <see cref="SpawnedEnemies"/>.</summary>
        private static void Share(SpawnController controller)
        {
            foreach (SpawnData data in controller.GetSpawnData())
            {
                if (data?._activeActors == null) continue;

                foreach (Actor actor in data._activeActors)
                {
                    if (actor == null) continue;

                    _standing.Add(actor.Pointer);
                    if (_recorded.Contains(actor.Pointer)) continue;

                    if (!TryAssetOf(actor, out string? asset))
                    {
                        MelonLogger.Warning($"[Spawner] '{actor.name}' came from a spawner but names no known creature; it stays on this machine.");
                        continue;
                    }

                    ActorSpawnAccess.RecordSpawn(actor, asset);
                    Shared++;
                }
            }
        }

        /// <summary>Client: runs this spawner only while the host is not in its volume and this player is.</summary>
        private static void Arbitrate(SpawnController controller, HitReceiveHandler? volume)
        {
            if (volume == null) return;

            bool hostInside = HostInside(volume);
            if (hostInside && controller.enabled)
            {
                controller.DeactivateSpawning();
                RemoveLocalWaves(controller);
                LeftToHost++;
                MelonLogger.Msg($"[Spawner] the host is at '{controller.name}'; its creatures replace the ones spawned here.");
            }
            // Only once the host is past the game's own despawn range, so its wave is gone there and an edge does not flap.
            else if (!hostInside && !controller.enabled && volume._isInCollider && HostFarFrom(volume))
            {
                controller.ActivateSpawning();
                Resumed++;
                MelonLogger.Msg($"[Spawner] the host left '{controller.name}'; spawning here again.");
            }
        }

        /// <summary>Takes this machine's own wave creatures away, with no corpse or loot, since the host's stand in for them.</summary>
        private static void RemoveLocalWaves(SpawnController controller)
        {
            foreach (SpawnData data in controller.GetSpawnData())
            {
                if (data?._activeActors == null) continue;

                foreach (Actor actor in data._activeActors)
                {
                    if (actor != null) UnityEngine.Object.Destroy(actor.gameObject);
                }

                data._activeActors.Clear();
            }
        }

        private static bool HostInside(HitReceiveHandler volume)
            => RemotePlayers.Bodies.TryGetValue(PlayerId.Host, out RemotePlayer? host) && host?.Body != null
                && GuardReach.Overlaps(volume, host.Body);

        private static bool HostFarFrom(HitReceiveHandler volume)
            => !RemotePlayers.Bodies.TryGetValue(PlayerId.Host, out RemotePlayer? host) || host?.Body == null
                || UnityEngine.Vector2.Distance(host.Body.transform.position, volume.transform.position) > HostGoneDistance;

        private static bool TryAssetOf(Actor actor, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? asset)
            => AssetByName.TryGetValue(actor.name.Replace("(Clone)", string.Empty).Trim(), out asset);

        private static void Forget()
        {
            Known.Clear();
            _recorded.Clear();
            _standing.Clear();
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Forget();

        void ICoopSystem.EndSession()
        {
            Shared = 0;
            LeftToHost = 0;
            Resumed = 0;
        }
    }
}
