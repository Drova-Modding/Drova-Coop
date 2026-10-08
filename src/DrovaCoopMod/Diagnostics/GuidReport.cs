#if DEBUG
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using UnityEngine;

namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// Reports whether each creature can still be found by the guid it reports.
    /// A runtime spawn can register under one guid before <c>GuidManager.Add</c> mints it a fresh one, so every lookup misses it.
    /// </summary>
    internal static class GuidReport
    {
        private const float DefaultRange = 60f;

        // Keeps the summary from scrolling off the console.
        private const int MaxListed = 24;

        internal static void Run(float range, Action<string> say)
        {
            if (range <= 0f) range = DefaultRange;

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null)
            {
                say("no entity handler; load a world first.");
                return;
            }

            Il2CppSystem.Collections.Generic.Dictionary<string, Actor> byGuid = entities._entityGuidDictionary;
            if (byGuid == null)
            {
                say("the entity handler has no guid table.");
                return;
            }

            // The whole table: a mismatched creature is only found by walking it.
            int total = 0;
            int mismatched = 0;

            foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Actor> pair in byGuid)
            {
                total++;

                Actor registered = pair.Value;
                if (registered == null) continue;

                string reported = EnemyRegistry.GuidOf(registered);

                if (!string.Equals(pair.Key, reported, StringComparison.Ordinal))
                {
                    mismatched++;

                    if (mismatched <= MaxListed)
                    {
                        say($"  MISMATCH {Name(registered)}: filed under {Short(pair.Key)}, reports " +
                            $"{Short(reported)} - every lookup by its own guid misses it.");
                    }
                }
            }

            say($"{total} actors registered; {mismatched} cannot be found by the guid they report.");

            if (mismatched > MaxListed)
            {
                say($"  ({mismatched - MaxListed} more mismatches not listed.)");
            }

            ReportNearby(entities, range, say);
        }

        private static void ReportNearby(EntityGameHandler entities, float range, Action<string> say)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player))
            {
                say("no player yet; the nearby list needs one.");
                return;
            }

            Vector2 origin = player.transform.position;
            float squared = range * range;

            Il2CppSystem.Collections.Generic.Dictionary<string, Actor> byGuid = entities._entityGuidDictionary;

            int listed = 0;
            int near = 0;

            say($"within {range:0} units:");

            foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Actor> pair in byGuid)
            {
                Actor actor = pair.Value;
                if (actor == null) continue;

                if (actor.Pointer == player.Pointer) continue;

                Vector2 position = actor.transform.position;
                if ((position - origin).sqrMagnitude > squared) continue;

                near++;

                if (listed >= MaxListed) continue;

                listed++;

                say("  " + Describe(entities, actor, pair.Key, origin));
            }

            if (near > listed)
            {
                say($"  ({near - listed} more nearby not listed; pass a smaller range.)");
            }

            if (near == 0)
            {
                say("  nothing.");
            }

            ReportHandles(entities, origin, range, say);
        }

        // Unbuilt creatures are missing from the actor table, e.g. ones near only a remote player on the host.
        private static void ReportHandles(
            EntityGameHandler entities, Vector2 origin, float range, Action<string> say)
        {
            Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppDrova.Utilities.LazyLoading.LazyActor> handles
                = entities._lazyActorGuidDictionary;

            if (handles == null) return;

            float squared = range * range;

            int listed = 0;
            int bodyless = 0;

            foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Il2CppDrova.Utilities.LazyLoading.LazyActor> pair
                in handles)
            {
                Il2CppDrova.Utilities.LazyLoading.LazyActor lazy = pair.Value;
                if (lazy == null || lazy.IsDestroyed) continue;

                if (lazy.IsLoaded) continue;

                Vector2 position = lazy.Position;
                if ((position - origin).sqrMagnitude > squared) continue;

                bodyless++;

                if (listed >= MaxListed) continue;

                listed++;

                float distance = Vector2.Distance(origin, position);

                say($"  NO BODY {lazy.name} {distance:0}u | guid {Short(pair.Key)} | " +
                    $"{(lazy.IsLoading ? "loading" : "not loaded")} | " +
                    $"save says {(lazy.IsDead() ? "dead" : "alive")} | cannot be claimed until it exists");
            }

            if (bodyless > 0)
            {
                say($"{bodyless} lazy handle(s) nearby have no body. On a host these are creatures the " +
                    "session cannot reach at all.");
            }

            if (bodyless > listed)
            {
                say($"  ({bodyless - listed} more not listed.)");
            }
        }

        private static string Describe(EntityGameHandler entities, Actor actor, string key, Vector2 origin)
        {
            string reported = EnemyRegistry.GuidOf(actor);

            Actor resolved = entities.GetActorFromGuid(reported);

            string roundTrip = resolved == null
                ? "LOOKUP FAILS"
                : resolved.Pointer == actor.Pointer ? "ok" : "LOOKUP HITS ANOTHER BODY";

            bool isLazy = entities.GetLazyActorFromGuid(reported) != null;
            bool isMinted = ActorSpawnAccess.IsRuntimeCreated(reported);

            string identity = isMinted
                ? "minted here (mod spawn)"
                : isLazy ? "authored (lazy handle)" : "runtime, no handle";

            Health health = actor.GetHealth();

            string state = health == null
                ? "no health"
                : $"{health.CurHealth}/{health.MaxHealth}{(health.IsDead ? " DEAD" : string.Empty)}" +
                  $"{(health.IsInvincible ? " invincible" : string.Empty)}";

            string session = DescribeSession(actor);

            float distance = Vector2.Distance(origin, actor.transform.position);

            return $"{Name(actor)} {distance:0}u | guid {Short(reported)}" +
                $"{(string.Equals(key, reported, StringComparison.Ordinal) ? string.Empty : $" (filed {Short(key)})")}" +
                $" | {roundTrip} | {identity} | {state}" +
                $"{(actor.IsInitialized ? string.Empty : " NOT-INITIALIZED")} | {DescribeAttention(actor)} | {session}";
        }

        // The target service only re-chooses while Actor.IsVisible, so FROZEN means it stopped re-choosing.
        // Read-only: querying the target service would run its aggro decorator and start fights.
        private static string DescribeAttention(Actor actor)
        {
            try
            {
                string sight = actor._visibleCallback == null
                    ? "no-cull"
                    : actor.IsVisible ? "watched" : "FROZEN";

                bool fighting = actor.AIModule != null && actor.AIModule.IsInCombat();

                return $"{sight}, {(fighting ? "in combat" : "idle")}";
            }
            catch (Exception)
            {
                return "attention unknown";
            }
        }

        private static string DescribeSession(Actor actor)
        {
            if (!CoopSession.IsLive)
            {
                return "no session";
            }

            if (!EnemyRegistry.TryGetSessionId(actor, out EnemyNetId id))
            {
                return "not in session";
            }

            if (CoopSession.State == CoopSessionState.Hosting)
            {
                return $"claimed as {id}{(EnemyRegistry.ClaimedIds.Contains(id) ? string.Empty : " NOT-BROADCAST")}";
            }

            return $"bound as {id}{(EnemyRegistry.IsDriven(id) ? ", driven" : ", not driven")}";
        }

        private static string Name(Actor actor)
        {
            try
            {
                return actor.name ?? "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Short(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "(none)";

            return guid.Length <= 8 ? guid : guid[..8];
        }
    }
}
#endif
