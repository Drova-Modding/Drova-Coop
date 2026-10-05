using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Kills the creatures other players killed.
    ///
    /// The far half of <see cref="EnemyDeathWatch"/>, and deliberately the whole of what it does: the
    /// health that got the creature there is not replicated, so what arrives is the conclusion rather than
    /// the fight. The other player's wolf may have been at full health on this screen a moment ago, and it
    /// will drop anyway. That is the honest shape of a first slice - the disagreement it removes is
    /// permanent, the one it leaves is not.
    ///
    /// Applied through <c>SetHealth</c>, which is normally the wrong call. Everywhere else in this mod
    /// health goes through <c>ChangeHealth</c>, because that is where the damage blockers live and one of
    /// them is what turns a killing blow into a knockout. Here the blockers are exactly what must not run:
    /// the host has already decided this creature is dead, and a local blocker that saved it would produce
    /// the one state the game cannot leave - alive here, dead there, forever. <c>SetHealth</c> ignores
    /// invincibility and the death flag, clamps, and still drives the game's whole death chain, so the
    /// animation, the ragdoll, the experience and the loot all happen locally as though this machine had
    /// landed the blow.
    /// </summary>
    internal static class RemoteEnemyDeath
    {
        /// <summary>
        /// Deaths whose body this machine has not got yet.
        ///
        /// Never dropped, and this is the common case rather than an edge. Each machine loads the
        /// world around its own player, nearest first, so a creature can be dead on one screen well before
        /// the other has streamed the chunk it stands in. Retried every frame until the body appears.
        /// </summary>
        private static readonly List<EnemyDeathMessage> _waiting = new();

        /// <summary>
        /// How many unresolved deaths are held.
        ///
        /// Bounded, unlike the queue that feeds it. The session's own cap governs messages that have
        /// arrived and not been taken; this list is what they become afterwards, and a death whose body
        /// never appears stays in it for the rest of the session. One that cannot be resolved is worth
        /// keeping - the chunk it lives in may simply not have streamed yet - but only up to the point
        /// where the list itself is the problem.
        /// </summary>
        private const int MaxWaiting = 512;

        /// <summary>
        /// How often the unresolved ones are tried again.
        ///
        /// Not every frame. Each attempt is a dictionary lookup across the interop boundary per waiting
        /// death, and what it is waiting for - a chunk of the world to stream in around this player - does
        /// not happen at frame rate. Four times a second is far faster than a body can appear.
        /// </summary>
        private const float RetrySeconds = 0.25f;

        private static float _sinceRetry;

        /// <summary>
        /// Applies everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            while (CoopSession.TryTakeEnemyDeath(out EnemyDeathMessage message))
            {
                // Recorded before anything else, even when the queue is full: the knowledge is the
                // message, not the body it will eventually land on. This is also what retires an earlier
                // "alive by the host's word" for the same creature - the applied kill happens inside a
                // remote-apply scope the death watch deliberately ignores, so nothing downstream of the
                // body would ever do it.
                EnemyRegistry.RecordDead(message.ActorGuid);
                CreditUnloadedDeath(message.ActorGuid);

                if (_waiting.Count >= MaxWaiting)
                {
                    MelonLogger.Warning($"[Enemy] more than {MaxWaiting} reported deaths are waiting for a body " +
                        $"that has not appeared; '{message.ActorGuid}' was dropped and that creature will stay " +
                        "alive here. This many usually means guids are arriving that this world cannot resolve.");
                    continue;
                }

                _waiting.Add(message);
            }

            if (_waiting.Count == 0) return;

            _sinceRetry += unscaledDeltaTime;
            if (_sinceRetry < RetrySeconds) return;

            _sinceRetry = 0f;

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            // Backwards, so removing the one that landed does not skip the next.
            for (int index = _waiting.Count - 1; index >= 0; index--)
            {
                if (Apply(entities, _waiting[index]))
                {
                    _waiting.RemoveAt(index);
                }
            }
        }

        /// <summary>
        /// Forgets what is waiting. Called when the world goes away, because the guids belong to a scene
        /// that no longer exists.
        /// </summary>
        internal static void Clear()
        {
            _waiting.Clear();
            _sinceRetry = 0f;
        }

        /// <summary>
        /// How many deaths of bodies not loaded here were counted by this world's kill observers anyway.
        /// For <c>coop_status</c>.
        /// </summary>
        internal static int Credited { get; private set; }

        /// <summary>
        /// Tells this world's kill-counting observers about a death whose body is not loaded here.
        ///
        /// Those observers count from the body's death event, or from the lazy actor's saved state at
        /// scene start. A teammate's kill of a creature this machine never streamed in raises neither -
        /// and once the death is recorded the creature never streams in at all - so a kill quest split
        /// between two players out of each other's range reached its total on neither machine until a
        /// reload: the Woodcamp rippers and sprigga, the Flussbett stalkers, the Brutus-cave golems.
        /// Counted by guid on the observer's own path, which ignores a guid it already holds.
        /// </summary>
        private static void CreditUnloadedDeath(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            try
            {
                if (EntityGameHandler.TryGet(out EntityGameHandler entities) && entities?.GetActorFromGuid(guid) != null) return;

                bool credited = false;

                // Inside the mirror scope: the killer's world makes the same writes, and a relative
                // one must not be applied twice (GVarDerived).
                using (RemoteApply<EnemyHealthScope>.Enter())
                {
                    RefreshObservers();

                    foreach (GlobalObserverBhvr_CountKilledActors counter in _counters!)
                    {
                        if (!Names(counter._lazyActors, guid)) continue;

                        counter.AddActorGuid(guid);
                        credited = true;
                    }

                    foreach (GlobalObserverBhvr_SetGVarAfterActorDead watcher in _watchers!)
                    {
                        if (!Names(watcher._actorsToCheck, guid)) continue;
                        if (!watcher._killedActorsIds.Add(guid)) continue;

                        credited = true;

                        if (watcher._condition != null && watcher._condition.CheckCondition())
                        {
                            watcher.CheckTriggerOperations();
                        }
                    }
                }

                if (credited) Credited++;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Enemy] crediting '{guid}' to this world's kill observers failed: " + e);
            }
        }

        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<GlobalObserverBhvr_CountKilledActors>? _counters;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<GlobalObserverBhvr_SetGVarAfterActorDead>? _watchers;
        private static int _observersFrame = -1;

        /// <summary>
        /// One scene scan per frame at most: a group killed at once, or a host's corpses streaming in,
        /// is many deaths in one frame, and two full scans each was a visible hitch.
        /// </summary>
        private static void RefreshObservers()
        {
            if (_observersFrame == UnityEngine.Time.frameCount && _counters != null && _watchers != null) return;

            _observersFrame = UnityEngine.Time.frameCount;
            _counters = UnityEngine.Object.FindObjectsOfType<GlobalObserverBhvr_CountKilledActors>();
            _watchers = UnityEngine.Object.FindObjectsOfType<GlobalObserverBhvr_SetGVarAfterActorDead>();
        }

        private static bool Names(Il2CppSystem.Collections.Generic.List<LazyActor> actors, string guid)
        {
            if (actors == null) return false;

            for (int index = 0; index < actors.Count; index++)
            {
                LazyActor actor = actors[index];
                if (actor != null && actor.GUID == guid) return true;
            }

            return false;
        }

        /// <summary>How many deaths are waiting for a body to arrive. For <c>coop_status</c>.</summary>
        internal static int Pending => _waiting.Count;

        /// <summary>
        /// Puts one reported death onto the creature it names.
        /// </summary>
        /// <returns>True when it landed or can never land; false to keep waiting for the body.</returns>
        private static bool Apply(EntityGameHandler entities, in EnemyDeathMessage message)
        {
            try
            {
                Actor actor = entities.GetActorFromGuid(message.ActorGuid);
                if (actor == null) return false;

                // A lazy body is findable by guid before its save has loaded. Killed then, it dies in memory
                // only - Saveable_Health is not listening yet - so the save still says alive and the creature
                // stands back up the next time it streams in. Wait for the body to finish.
                if (!EnemyRegistry.IsReady(actor)) return false;

                Health health = actor.GetHealth();

                // No health is not a body that has not arrived; it is one that will never be killable.
                // Kept out of the waiting list rather than retried forever.
                if (health == null) return true;

                if (health.IsDead) return true;

                // A creature whose brawl is leased to the reporter stands here invincible, and the kill would land
                // on that shield and be dropped as done: the duel's loser died on the client and got up again on
                // the host when the lease let go. Waited for until the lease is released, which lifts the shield.
                if (EnemyRegistry.TryGetId(actor, out EnemyNetId leased) && EnemyEngagements.TryGetLease(leased, out _)) return false;

                // No veto for a local damage blocker. SetHealth ignores blockers on purpose: the session
                // synchronizes quest state so both worlds stand at the same point in the story, and the
                // machine that reported this death is the one whose world resolved it. A blocker still
                // installed here is this world lagging the shared story, not disagreeing with it.
                bool isReconcile = message.IsReconcile;

                // The game's own switch for the difference between dying and having died. A corpse that
                // streams in beside a player must not play its death animation, award its experience or
                // cast whatever some creatures cast as they fall - all of which hang off the same event
                // this write is about to raise. It is set on the args rather than on the change because
                // the args are what every listener receives.
                Action<HealthChangeArgs> markLoaded = new(args =>
                {
                    if (args != null && isReconcile)
                    {
                        args.SetIsLoadingArg(true);
                    }
                });

                using (RemoteApply<EnemyHealthScope>.Enter())
                {
                    health.SetHealth(0, null, markLoaded);
                }

                return true;
            }
            catch (Exception e)
            {
                // Swallowed, and the death is dropped rather than retried: an exception here repeats every
                // frame for the rest of the session, and a log filling at frame rate is worse than one
                // creature left standing.
                MelonLogger.Error($"[Enemy] killing '{message.ActorGuid}' failed: " + e);
                return true;
            }
        }
    }
}
