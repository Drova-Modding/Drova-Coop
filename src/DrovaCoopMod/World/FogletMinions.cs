using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// The creatures an ability spawns straight from a prefab (<c>ABI_Bhvr_SpawnEntity</c>) - a foglet's fog duplicates -
    /// made one set for the party. The host runs the creature's AI, so the host's spawn is the real one: it is done here
    /// the way the game does it, and each new body is shared through <see cref="SpawnedEnemies"/>. A client whose copy of
    /// the caster is driven by the host spawns nothing of its own; it builds what the host announces.
    ///
    /// The same for a creature's conjure (<c>ABI_Bhvr_Conjure</c>) - the arena champions' rippers, insects and
    /// doppelgangers: the copy that only mirrors a fight replayed the cast and conjured a second set of its own, which
    /// fought the watching player on that screen alone. A player's conjure is SummonWatch's and left to it.
    /// </summary>
    internal static class FogletMinions
    {
        /// <summary>Spawned bodies shared from here. For tests.</summary>
        internal static int Shared { get; private set; }

        /// <summary>Conjures a session creature started here, whoever's they turned out to be. For tests.</summary>
        internal static int Cast { get; private set; }

        /// <summary>Spawns skipped here because the host's are the real ones. For tests.</summary>
        internal static int Skipped { get; private set; }

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(Il2CppDrova.ABI.ABI_Bhvr_SpawnEntity), "Spawn", typeof(FogletMinions), nameof(SpawnPrefix));
            System.Reflection.MethodInfo spawnPrefab = HarmonyLib.AccessTools.Method(typeof(Il2CppDrova.ABI.ABI_Bhvr_Conjure), "SpawnPrefab",
                [typeof(GameObject), typeof(Entity), typeof(Vector2)]);
            Hooking.TryPrefix(harmony, spawnPrefab, typeof(FogletMinions), nameof(ConjurePrefix), "ABI_Bhvr_Conjure.SpawnPrefab");
            Hooking.TryPostfix(harmony, spawnPrefab, typeof(FogletMinions), nameof(ConjurePostfix), "ABI_Bhvr_Conjure.SpawnPrefab");
        }

        /// <summary>
        /// Whether this machine's copy of a creature only mirrors a fight run elsewhere: a client's copy the host drives,
        /// or the host's copy of a client's leased fight. Its casts are replays, and their spawns belong to the real one.
        /// </summary>
        internal static bool Mirrors(Actor owner)
        {
            if (!EnemyRegistry.TryGetSessionId(owner, out Messages.EnemyNetId id)) return false;
            return CoopSession.State == CoopSessionState.Joined ? EnemyRegistry.IsDriven(id) : EnemyEngagements.TryGetLease(id, out _);
        }

        private static bool ConjurePrefix(GameObject prefab, Entity owner)
        {
            try
            {
                if (!CoopSession.IsLive || prefab == null) return true;

                // The real caster conjures: the host's creature on the host, and in a client's leased fight the client's live
                // copy. A copy that only mirrors the fight conjures nothing; the host shares what its own conjure.
                // The host's copy of a client's leased caster never casts (casts do not cross in the snapshots); the client
                // reports its summons instead (LeaseSummons).
                Actor? caster = owner?.TryCast<Actor>();
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

        /// <summary>The host's creature conjured: the summon is shared like any spawned creature. A client's leased one: reported.</summary>
        private static void ConjurePostfix(Il2CppDrova.ABI.ABI_Bhvr_Conjure __instance, GameObject prefab, Entity owner)
        {
            try
            {
                if (!CoopSession.IsLive || prefab == null || __instance == null) return;

                Actor? caster = owner?.TryCast<Actor>();
                if (caster == null || RemotePlayers.IsPlayersBody(caster) || !EnemyRegistry.TryGetSessionId(caster, out Messages.EnemyNetId casterId)) return;

                Il2CppSystem.Collections.Generic.List<GameObject> made = __instance._spawnedInstances;
                Actor? body = made != null && made.Count > 0 ? made[made.Count - 1]?.GetComponent<Actor>() : null;
                if (body == null) return;

                if (CoopSession.State == CoopSessionState.Joined)
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

        private static bool SpawnPrefix(Il2CppDrova.ABI.ABI_Bhvr_SpawnEntity __instance)
        {
            try
            {
                if (!CoopSession.IsLive || __instance == null) return true;

                Actor? owner = __instance._controller?.AbilityInfo?.GetOwner()?.TryCast<Actor>();
                if (owner == null || __instance._prefab == null) return true;

                // A client's copy of a host creature: its spawns are the host's to make.
                if (CoopSession.State == CoopSessionState.Joined && EnemyRegistry.TryGetSessionId(owner, out Messages.EnemyNetId ownerId) && EnemyRegistry.IsDriven(ownerId))
                {
                    Skipped++;
                    return false;
                }

                // Only a creature of the session: the host's own player, and creatures nobody shares, spawn as the game does.
                if (CoopSession.State != CoopSessionState.Hosting || !EnemyRegistry.TryGetSessionId(owner, out _)) return true;

                // The game's own spawn, kept hold of: one at the precomputed spot, or several around the owner.
                string prefab = __instance._prefab.name;
                int count = __instance._numSpawns;
                for (int index = 0; index < count; index++)
                {
                    Vector2 at = count == 1 ? __instance._singleSpawnPos : __instance.CalcSpawnPosition(__instance._controller.AbilityInfo);
                    Entity spawned = UnityEngine.Object.Instantiate(__instance._prefab, at, Quaternion.identity);
                    if (__instance._vfxPrefab != null) UnityEngine.Object.Instantiate(__instance._vfxPrefab, at, Quaternion.identity);

                    Actor? body = spawned?.TryCast<Actor>();
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
