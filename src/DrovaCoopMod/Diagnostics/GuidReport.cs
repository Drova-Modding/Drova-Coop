#if DEBUG
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.World;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Diagnostics
{
    /// <summary>
    /// Answers the one question the enemy synchronization cannot ask itself: whether a creature can still
    /// be found by the name this mod filed it under.
    ///
    /// The failure it exists for does not look like a failure anywhere. Everything this mod does with a
    /// creature that is not a player goes through a guid: it is what a claim is keyed by, what a bind
    /// carries, what a death is reported as, and what every resolution back to a body goes through. Nothing
    /// checks that the last of those agrees with the first, because for a creature authored into a scene it
    /// always does - the streaming system unregisters, stamps the authored guid and registers again, in that
    /// order and on purpose.
    ///
    /// For a creature spawned at runtime it need not agree at all. <c>Entity.Awake</c> registers the body in
    /// the handler's dictionary under whatever <c>GuidString</c> reads at that moment, and
    /// <c>GuidComponent.Awake</c> is a separate Awake on the same object with no defined order between them.
    /// Worse, it does not merely confirm the baked guid: <c>GuidManager.Add</c> refuses a duplicate, so the
    /// *second* instance of a prefab has <c>CreateGuid</c> mint a fresh one - after the registration that
    /// used the old one. The dictionary is then keyed by a string the actor no longer reports, and every
    /// lookup for that creature returns nothing while the creature stands there in plain sight.
    ///
    /// So this reports the round trip rather than the guid. A guid on its own says nothing; a guid that
    /// cannot find its own body is the whole bug, and it is one line.
    /// </summary>
    internal static class GuidReport
    {
        /// <summary>How far around the player creatures are reported by default.</summary>
        private const float DefaultRange = 60f;

        /// <summary>
        /// How many creatures are listed before the rest are summarized.
        ///
        /// The console holds a few dozen lines before the earliest scroll off, and a report that pushes its
        /// own summary off the top is worse than one that stops.
        /// </summary>
        private const int MaxListed = 24;

        /// <summary>
        /// Writes the report.
        /// </summary>
        /// <param name="range">How far around the player to look. Zero or less uses the default.</param>
        /// <param name="say">Where each line goes.</param>
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

            // The whole table, not only what is nearby. A creature whose key no longer matches its own
            // guid is invisible to a search by guid, so it can only be found by walking the table itself -
            // which is also the only way to notice one that is far away and still broken.
            int total = 0;
            int mismatched = 0;

            foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Actor> pair in byGuid)
            {
                total++;

                Actor registered = pair.Value;
                if (registered == null) continue;

                string reported = registered.GetGuidComponent()?.GuidString ?? string.Empty;

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

        /// <summary>
        /// Lists what is around the player, with what this mod believes about each one.
        /// </summary>
        private static void ReportNearby(EntityGameHandler entities, float range, Action<string> say)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null)
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

        /// <summary>
        /// Lists the lazy handles nearby that have no body.
        ///
        /// The blind spot the rest of this report has by construction. Everything above walks the
        /// handler's table of registered actors, which contains only creatures that currently exist. A
        /// creature the streaming system has not built has no entry there and cannot appear in any of it -
        /// so a machine that is missing a creature entirely produces a report that looks perfectly healthy.
        ///
        /// That is exactly the state a host is in for creatures only a remote player is standing near:
        /// Drova decides what to build from the local player's position, the sweep can only claim what has
        /// been built, and nothing builds what only somebody else can see. The result is a creature that is
        /// never claimed, never bound, never sent - and on the other machine either absent or, if that
        /// player's own save killed it, a corpse that nothing will ever stand back up.
        /// </summary>
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

                // A handle with a body is already covered by the list above, under that body.
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

        /// <summary>
        /// One creature, as every part of this mod sees it.
        /// </summary>
        private static string Describe(EntityGameHandler entities, Actor actor, string key, Vector2 origin)
        {
            string reported = actor.GetGuidComponent()?.GuidString ?? string.Empty;

            // The round trip, which is the point of the whole command: filed under one name, asked for by
            // the name it gives out, and either the same body comes back or something is wrong.
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

        /// <summary>
        /// Whether this creature is still choosing who to fight, and whether it thinks it is fighting.
        ///
        /// The one fact that cannot be read out of the game's code. A creature's target service only
        /// re-chooses while <c>Actor.IsVisible</c> is true, and that is Unity's renderer visibility, held
        /// by a <c>BecameVisibleCallback</c> which is a hand-assigned serialized field - so whether a given
        /// creature has one at all is prefab data. <c>no-cull</c> means it has none and was always going to
        /// keep thinking; <c>FROZEN</c> means it has one, is off this machine's screen, and has stopped
        /// re-choosing - which for a creature next to a teammate is the state
        /// <see cref="World.EnemyPinning"/> exists to prevent, and seeing it on a claimed creature means
        /// that did not take.
        ///
        /// Read-only on purpose. Asking the target service who it has picked would run the whole chain and
        /// its aggro decorator, which files targets into combat sets - a report that starts fights is not
        /// a report.
        /// </summary>
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

        /// <summary>
        /// What the session makes of this creature, from whichever end this machine is.
        /// </summary>
        private static string DescribeSession(Actor actor)
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                return "no session";
            }

            if (!EnemyRegistry.TryGetSessionId(actor, out EnemyNetId id))
            {
                return "not in session";
            }

            if (CoopSession.State == CoopSessionState.Hosting)
            {
                return $"claimed as {id}{(CoopSession.ClaimedEnemies.Contains(id) ? string.Empty : " NOT-BROADCAST")}";
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

        /// <summary>
        /// The first eight characters of a guid. Enough to match two lines against each other by eye, which
        /// is what this report is read for, and short enough that a line still fits the console.
        /// </summary>
        private static string Short(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "(none)";

            return guid.Length <= 8 ? guid : guid[..8];
        }
    }
}
#endif
