using Drova_Modding_API;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// One set of ability spawns (<c>ABI_Bhvr_SpawnEntity</c>, foglet duplicates) and creature conjures for the party.
    /// The real caster spawns and shares through <see cref="SpawnedEnemies"/>. A mirroring copy spawns nothing.
    /// </summary>
    internal sealed class FogletMinions : ICoopSystem
    {
        /// <summary>Spawned bodies shared from here. For tests.</summary>
        internal static int Shared { get; private set; }

        /// <summary>Conjures a session creature started here, whoever's they turned out to be. For tests.</summary>
        internal static int Cast { get; private set; }

        /// <summary>Spawns skipped here because the host's are the real ones. For tests.</summary>
        internal static int Skipped { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(Il2CppDrova.ABI.ABI_Bhvr_SpawnEntity), "Spawn", typeof(FogletMinionsPatches), nameof(FogletMinionsPatches.SpawnPrefix));
            System.Reflection.MethodInfo spawnPrefab = HarmonyLib.AccessTools.Method(typeof(Il2CppDrova.ABI.ABI_Bhvr_Conjure), "SpawnPrefab",
                [typeof(GameObject), typeof(Entity), typeof(Vector2)]);
            Hooking.TryPrefix(harmony, spawnPrefab, typeof(FogletMinionsPatches), nameof(FogletMinionsPatches.ConjurePrefix), "ABI_Bhvr_Conjure.SpawnPrefab");
            Hooking.TryPostfix(harmony, spawnPrefab, typeof(FogletMinionsPatches), nameof(FogletMinionsPatches.ConjurePostfix), "ABI_Bhvr_Conjure.SpawnPrefab");
        }

        /// <summary>
        /// Whether this copy of a creature only mirrors a fight run elsewhere (host-driven on a client, or a client's lease on the host).
        /// </summary>
        internal static bool Mirrors(Actor owner)
        {
            if (!EnemyRegistry.TryGetSessionId(owner, out Messages.EnemyNetId id)) return false;
            return CoopSession.JoinedWorld ? EnemyRegistry.IsDriven(id) : EnemyEngagements.TryGetLease(id, out _);
        }

        internal static bool ConjurePrefix(GameObject prefab, Entity? owner)
        {
            try
            {
                if (!CoopSession.SharesWorld || prefab == null) return true;

                // Only the real caster conjures. A leased client reports its summons through LeaseSummons.
                Actor? caster = owner == null ? null : owner.TryCast<Actor>();
                if (caster == null || RemotePlayers.IsPlayersBody(caster) || !EnemyRegistry.TryGetSessionId(caster, out _)) return true;

                Cast++;
                if (!Mirrors(caster)) return true;

                Skipped++;
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] holding back a mirrored creature's conjure failed: " + e);
                return true;
            }
        }

        /// <summary>Shares a host creature's summon, or reports a client's leased one.</summary>
        internal static void ConjurePostfix(Il2CppDrova.ABI.ABI_Bhvr_Conjure __instance, GameObject prefab, Entity? owner)
        {
            try
            {
                if (!CoopSession.SharesWorld || prefab == null || __instance == null) return;

                Actor? caster = owner == null ? null : owner.TryCast<Actor>();
                if (caster == null || RemotePlayers.IsPlayersBody(caster) || !EnemyRegistry.TryGetSessionId(caster, out Messages.EnemyNetId casterId)) return;

                Il2CppSystem.Collections.Generic.List<GameObject>? made = __instance._spawnedInstances;
                GameObject? last = made != null && made.Count > 0 ? made[made.Count - 1] : null;
                Actor? body = last == null ? null : last.GetComponent<Actor>();
                if (body == null) return;

                if (CoopSession.JoinedWorld)
                {
                    if (EngageWatch.IsBrawling(casterId)) LeaseSummons.Report(body, prefab.name);
                    return;
                }

                SpawnedEnemies.ShareInstantiated(body, prefab.name);
                Shared++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] sharing a creature's conjure failed: " + e);
            }
        }

        internal static bool SpawnPrefix(Il2CppDrova.ABI.ABI_Bhvr_SpawnEntity __instance)
        {
            try
            {
                if (!CoopSession.SharesWorld || __instance == null) return true;

                Entity? ownerEntity = __instance._controller?.AbilityInfo?.GetOwner();
                Actor? owner = ownerEntity == null ? null : ownerEntity.TryCast<Actor>();
                if (owner == null || __instance._prefab == null) return true;

                if (CoopSession.JoinedWorld && EnemyRegistry.TryGetSessionId(owner, out Messages.EnemyNetId ownerId) && EnemyRegistry.IsDriven(ownerId))
                {
                    Skipped++;
                    return false;
                }

                if (!CoopSession.HostsWorld || !EnemyRegistry.TryGetSessionId(owner, out _)) return true;

                // The game's own spawn, redone here to keep hold of the bodies.
                string prefab = __instance._prefab.name;
                int count = __instance._numSpawns;
                for (int index = 0; index < count; index++)
                {
                    Vector2 at = count == 1 ? __instance._singleSpawnPos : __instance.CalcSpawnPosition(__instance._controller.AbilityInfo);
                    Entity? spawned = UnityEngine.Object.Instantiate(__instance._prefab, at, Quaternion.identity);
                    if (__instance._vfxPrefab != null) UnityEngine.Object.Instantiate(__instance._vfxPrefab, at, Quaternion.identity);

                    Actor? body = spawned == null ? null : spawned.TryCast<Actor>();
                    if (body == null) continue;

                    SpawnedEnemies.ShareInstantiated(body, prefab);
                    Shared++;
                }

                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] sharing an ability's spawn failed: " + e);
                return true;
            }
        }
    }
}
