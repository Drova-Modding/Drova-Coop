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
    /// Kills the creatures other players killed, the far half of <see cref="EnemyDeathWatch"/>. Uses
    /// <c>SetHealth</c>, which skips damage blockers but still runs the game's whole death chain.
    /// </summary>
    internal static class RemoteEnemyDeath
    {
        /// <summary>
        /// Deaths whose body has not streamed in here yet, retried until it does.
        /// </summary>
        private static readonly List<EnemyDeathMessage> Waiting = new();

        /// <summary>How many unresolved deaths are held, since one whose body never appears stays all session.</summary>
        private const int MaxWaiting = 512;

        /// <summary>How often the unresolved ones are tried again.</summary>
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
                // Recorded first, even when the queue is full. This also retires an earlier "alive by
                // the host's word", since the death watch ignores the remote-apply scope of the kill.
                EnemyRegistry.RecordDead(message.ActorGuid);
                CreditUnloadedDeath(message.ActorGuid);

                if (Waiting.Count >= MaxWaiting)
                {
                    MelonLogger.Warning($"[Enemy] more than {MaxWaiting} reported deaths are waiting for a body " +
                        $"that has not appeared; '{message.ActorGuid}' was dropped and that creature will stay " +
                        "alive here. This many usually means guids are arriving that this world cannot resolve.");
                    continue;
                }

                Waiting.Add(message);
            }

            if (Waiting.Count == 0) return;

            _sinceRetry += unscaledDeltaTime;
            if (_sinceRetry < RetrySeconds) return;

            _sinceRetry = 0f;

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            for (int index = Waiting.Count - 1; index >= 0; index--)
            {
                if (Apply(entities, Waiting[index]))
                {
                    Waiting.RemoveAt(index);
                }
            }
        }

        /// <summary>
        /// Forgets what is waiting. Called when the world goes away.
        /// </summary>
        internal static void Clear()
        {
            Waiting.Clear();
            _sinceRetry = 0f;
        }

        /// <summary>
        /// How many deaths of bodies not loaded here were credited to kill observers. For <c>coop_status</c>.
        /// </summary>
        internal static int Credited { get; private set; }

        /// <summary>
        /// Credits kill-counting observers with a death whose body is not loaded here. They only count
        /// from a body's death event or saved state, so split kill quests would otherwise never complete.
        /// </summary>
        private static void CreditUnloadedDeath(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            try
            {
                if (EntityGameHandler.TryGet(out EntityGameHandler entities) && entities?.GetActorFromGuid(guid) != null) return;

                bool credited = false;

                // Inside the mirror scope: the killer's world makes the same writes (GVarDerived).
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
        /// One scene scan per frame at most, since many deaths can arrive in one frame.
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
        internal static int Pending => Waiting.Count;

        /// <returns>True when it landed or can never land, false to keep waiting for the body.</returns>
        private static bool Apply(EntityGameHandler entities, in EnemyDeathMessage message)
        {
            try
            {
                Actor actor = entities.GetActorFromGuid(message.ActorGuid);
                if (actor == null) return false;

                // A lazy body is findable before its save loads. Killed then, Saveable_Health misses it
                // and the creature stands back up next stream-in.
                if (!EnemyRegistry.IsReady(actor)) return false;

                Health health = actor.GetHealth();

                // No health means never killable, so stop waiting.
                if (health == null) return true;

                if (health.IsDead) return true;

                // A leased brawl creature is invincible here, so wait until the lease lifts the shield.
                if (EnemyRegistry.TryGetId(actor, out EnemyNetId leased) && EnemyEngagements.TryGetLease(leased, out _)) return false;

                bool isReconcile = message.IsReconcile;

                // A reconciled corpse loads as already dead: no death animation, experience or death cast.
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
                // Dropped rather than retried, or the exception would repeat every frame.
                MelonLogger.Error($"[Enemy] killing '{message.ActorGuid}' failed: " + e);
                return true;
            }
        }
    }
}
