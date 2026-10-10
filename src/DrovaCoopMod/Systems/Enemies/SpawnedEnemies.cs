using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Spawning;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Makes creatures that mods spawn at runtime exist for everybody, by replicating the spawn recipe rather than the creature.
    /// Build-time rolls (names, equipment) differ per machine. Kind, position, health and death match.
    /// </summary>
    internal sealed class SpawnedEnemies : ICoopSystem
    {
        /// <summary>The recipe prefix for a creature built from a prefab by name rather than from an addressable asset.</summary>
        internal const string PrefabRecipe = "prefab:";

        // Repeated because the first announcement can land before the newcomer's session is Joined.
        private const int AnnounceRepeats = 6;

        private const float RepeatSeconds = 1f;

        private readonly record struct Noticed(Actor? Body, LazyActor? Handle, string AssetGuid);

        // The asset is kept so a late joiner can be told what an already-standing creature is.
        private readonly struct Announced(EnemyNetId id, string assetGuid, Actor actor)
        {
            internal EnemyNetId Id { get; } = id;

            internal string AssetGuid { get; } = assetGuid;

            internal Actor Actor { get; } = actor;
        }

        // Host: what has been announced. Client: what has been built here per handle, so a repeat does not build a second body.
        private static readonly Dictionary<IntPtr, Announced> AnnouncedSpawns = new();
        private static readonly Dictionary<EnemyNetId, Actor> Built = new();

        // Spawns noticed but not yet shared. Exactly one of Body and Handle is set.
        private static readonly Dictionary<IntPtr, Noticed> NoticedSpawns = new();

        // Cached: a full scan of every loaded actor is too costly per spawned minion.
        private static readonly Dictionary<string, Actor> Prefabs = new(StringComparer.Ordinal);

        private static bool _subscribed;
        private static int _lastRosterCount;
        private static int _repeatsLeft;
        private static float _sinceRepeat;

        private static readonly Queue<EnemySpawnMessage> IncomingSpawns = new();

        string? ICoopSystem.HookSwitch => null;

        /// <summary>How many announced spawns this game could not build. Non-zero with identical mod sets means the asset id was lost.</summary>
        internal static int Unbuildable { get; private set; }

        /// <summary>Spawns this host announced. For tests.</summary>
        internal static int AnnouncedCount { get; private set; }

        /// <summary>Spawns this client built from an announcement. For tests.</summary>
        internal static int BuiltCount { get; private set; }

        /// <summary>How many spawned creatures this machine is sharing or has built.</summary>
        internal static int Count => AnnouncedSpawns.Count + Built.Count;

        /// <summary>Spawns noticed but not shared yet. A lazy handle nobody walked near sits here indefinitely.</summary>
        internal static int Pending => NoticedSpawns.Count;

        /// <summary>Starts listening for spawns.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            if (_subscribed) return;

            ActorSpawnAccess.OnActorSpawned += OnActorSpawned;
            ActorSpawnAccess.OnLazyActorSpawned += OnLazyActorSpawned;
            _subscribed = true;
        }

        /// <summary>Builds whatever the host has announced.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (CoopSession.HostsWorld)
            {
                WatchPending();
                AnnounceToNewcomers(unscaledDeltaTime);
                return;
            }

            if (!CoopSession.JoinedWorld) return;

            // Only the host announces, so anything a client noticed would never be shared.
            NoticedSpawns.Clear();

            while (TryTakeEnemySpawn(out EnemySpawnMessage message))
            {
                Build(message);
            }
        }

        // A spawn happens once, so a late joiner is re-told everything standing. The far side ignores handles it already built.
        private static void AnnounceToNewcomers(float unscaledDeltaTime)
        {
            int roster = CoopSession.Players.Count;

            if (roster != _lastRosterCount)
            {
                _lastRosterCount = roster;

                // Several times, not once: the newcomer's receive path drops everything until its session is Joined.
                _repeatsLeft = AnnounceRepeats;
                _sinceRepeat = 0f;
            }

            if (_repeatsLeft <= 0) return;

            _sinceRepeat += unscaledDeltaTime;
            if (_sinceRepeat < RepeatSeconds) return;

            _sinceRepeat = 0f;
            _repeatsLeft--;

            if (AnnouncedSpawns.Count == 0) return;

            foreach (KeyValuePair<IntPtr, Announced> announced in AnnouncedSpawns)
            {
                Actor? actor = announced.Value.Actor;

                // Death travels on its own channel. Re-announcing a corpse would only ask somebody to build one.
                if (actor == null)
                {
                    AnnouncedSpawns.Remove(announced.Key);
                    continue;
                }

                Health health = actor.GetHealth();
                if (health != null && health.IsDead)
                {
                    AnnouncedSpawns.Remove(announced.Key);
                    continue;
                }

                BroadcastEnemySpawn(announced.Value.Id, announced.Value.AssetGuid, actor.transform.position);
            }

            if (_repeatsLeft == AnnounceRepeats - 1)
            {
                MelonLogger.Msg($"[Enemy] telling the session about {AnnouncedSpawns.Count} creature(s) already " +
                    $"standing here, {AnnounceRepeats} times over the next {AnnounceRepeats * RepeatSeconds:0}s " +
                    "so a machine still joining does not miss them.");
            }
        }

        /// <summary>Removes the copy built here for a host creature that no longer exists, without corpse, loot or experience.</summary>
        internal static void Unbuild(EnemyNetId id)
        {
            if (!Built.Remove(id, out Actor? actor)) return;
            if (actor != null) UnityEngine.Object.Destroy(actor.gameObject);
        }

        /// <summary>Forgets everything and removes the client's copies. The host's spawns belong to its world and stay.</summary>
        internal static void Clear()
        {
            foreach (KeyValuePair<EnemyNetId, Actor> built in Built)
            {
                ActorSpawnAccess.TryKill(built.Value);
            }

            Built.Clear();
            AnnouncedSpawns.Clear();
            _lastRosterCount = 0;
            _repeatsLeft = 0;
            _sinceRepeat = 0f;

            NoticedSpawns.Clear();
        }

        // A lazy handle builds and destroys its body as players come and go, so only the asset is kept until a body exists.
        private static void OnLazyActorSpawned(LazyActor lazyActor, string assetGuid)
        {
            try
            {
                if (lazyActor == null || string.IsNullOrEmpty(assetGuid)) return;

                // Kept even without a session: a handle created before somebody joins must still be shared later.
                NoticedSpawns[lazyActor.Pointer] = new Noticed(null, lazyActor, assetGuid);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] noticing a lazy creature another mod created failed: " + e);
            }
        }

        // Host only. Handles are polled, not subscribed to, so no interop delegate is held across scene loads.
        private static void WatchPending()
        {
            foreach (KeyValuePair<IntPtr, Noticed> pending in NoticedSpawns)
            {
                (Actor? body, LazyActor? handle, string assetGuid) = pending.Value;

                bool gone = handle == null ? body == null : handle.IsDestroyed;

                if (gone)
                {
                    NoticedSpawns.Remove(pending.Key);
                    continue;
                }

                // A handle with no body yet may stay that way forever, so it is kept rather than expired.
                if (handle != null)
                {
                    body = handle.Actor;
                }

                // A creature is claimed by the guid its components get as it initializes.
                if (!EnemyRegistry.IsReady(body)) continue;

                NoticedSpawns.Remove(pending.Key);

                if (AnnouncedSpawns.ContainsKey(body.Pointer)) continue;

                // A JSON-placed NPC (MageDrova's Rowan) stands on every machine already; a copy would be a second, undressed
                // body. Checked here, not when noticed: the placement is registered only after the spawn event fires.
                // Code-spawned NPCs (Random Events' bandits, host only) are still shared.
                if (ExternalNpcPlacementSystem.IsPlaced(handle)) continue;

                Share(body, assetGuid);
            }
        }

        private static void OnActorSpawned(Actor actor, string assetGuid)
        {
            try
            {
                if (actor == null || string.IsNullOrEmpty(assetGuid)) return;

                if (AnnouncedSpawns.ContainsKey(actor.Pointer)) return;

                // Noticed even without a session: anything spawned before somebody joined must still be shared.
                NoticedSpawns[actor.Pointer] = new Noticed(actor, null, assetGuid);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] noticing a spawned creature failed: " + e);
            }
        }

        private static void Share(Actor actor, string assetGuid)
        {
            if (!EnemyRegistry.TryClaimSpawned(actor, out EnemyNetId id)) return;

            AnnouncedSpawns[actor.Pointer] = new Announced(id, assetGuid, actor);

            Vector2 position = actor.transform.position;

            BroadcastEnemySpawn(id, assetGuid, position);
            AnnouncedCount++;

            MelonLogger.Msg($"[Enemy] a spawned creature is now shared as {id}, built from '{assetGuid}'.");
        }

        /// <summary>Shares a creature the game instantiated straight from a prefab (a foglet's fog duplicates), which no asset guid names.</summary>
        internal static void ShareInstantiated(Actor actor, string prefabName)
        {
            if (actor == null || string.IsNullOrEmpty(prefabName) || !CoopSession.SharesWorld) return;
            Share(actor, PrefabRecipe + prefabName);
        }

        /// <summary>Builds a creature from a <see cref="PrefabRecipe"/> recipe.</summary>
        internal static bool TryBuildFromPrefab(string recipe, Vector2 position, [NotNullWhen(true)] out Actor? actor)
        {
            actor = null;
            string name = recipe.Substring(PrefabRecipe.Length);

            if (!Prefabs.TryGetValue(name, out Actor? prefab) || prefab == null)
            {
                prefab = null;
                foreach (Actor candidate in Resources.FindObjectsOfTypeAll<Actor>())
                {
                    // The prefab asset itself: an object of no scene.
                    if (candidate == null || candidate.name != name || candidate.gameObject.scene.IsValid()) continue;

                    prefab = candidate;
                    break;
                }

                if (prefab == null) return false;
                Prefabs[name] = prefab;
            }

            actor = UnityEngine.Object.Instantiate(prefab.gameObject, new Vector3(position.x, position.y, 0f), Quaternion.identity).GetComponent<Actor>();
            return actor != null;
        }

        private static void Build(in EnemySpawnMessage message)
        {
            try
            {
                if (!message.IsPlausible()) return;
                if (Built.ContainsKey(message.NetId)) return;

                // This client's own summon, built here first and announced back by the host: bound, not built twice.
                if (LeaseSummons.TryAdopt(message)) return;

                Vector2 at = new(message.X, message.Y);
                Actor actor;
                bool built = message.AssetGuid.StartsWith(PrefabRecipe, StringComparison.Ordinal)
                    ? TryBuildFromPrefab(message.AssetGuid, at, out actor)
                    : ActorSpawnAccess.TrySpawnFromAsset(message.AssetGuid, at, out actor);
                if (!built)
                {
                    Unbuildable++;

                    MelonLogger.Warning($"[Enemy] the other player spawned '{message.AssetGuid}', which this " +
                        "game could not build. Mod sets may differ; that creature will not appear here.");
                    return;
                }

                Built[message.NetId] = actor;
                BuiltCount++;

                MelonLogger.Msg($"[Enemy] built {message.NetId} from '{message.AssetGuid}' at " +
                    $"({message.X:0}, {message.Y:0}) because the other player spawned it.");

                // Bound to the host's handle, so health, position and death apply as to any shared creature.
                EnemyRegistry.BindSpawnedBody(message.NetId, actor);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] building a creature the other player spawned failed: " + e);
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<EnemySpawnMessage>(MessageIds.EnemySpawn,
                (_, m) => CoopSession.QueueFromHost(m, IncomingSpawns, CoopSession.MaxPendingFromHost, "spawned creatures"));
        }

        void ICoopSystem.EndSession()
        {
            // A spawn built after the session ends would stand in a world its announcement no longer describes.
            IncomingSpawns.Clear();
        }

        /// <summary>Tells everyone about a creature this machine spawned at runtime.</summary>
        internal static void BroadcastEnemySpawn(EnemyNetId id, string assetGuid, Vector2 position)
        {
            if (!CoopSession.HostsWorld) return;

            NetworkAccess.SendToAll(new EnemySpawnMessage
            {
                NetId = id,
                AssetGuid = assetGuid,
                X = position.x,
                Y = position.y,
            });
        }

        internal static bool TryTakeEnemySpawn(out EnemySpawnMessage message)
            => IncomingSpawns.TryDequeue(out message);

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Clear();
        }
    }
}
