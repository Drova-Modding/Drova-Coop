using Drova_Modding_API;
using Drova_Modding_API.Access;
using Il2Cpp;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Asks the host about creatures this world believes dead, so ones the host killed get laid out here too.
    /// Ones the host has alive are brought back by <see cref="EnemyRegistry"/>.
    /// </summary>
    internal static class HostWorldState
    {
        private const int BatchSize = WorldStateRequestMessage.MaxGuids;

        /// <summary>How long a guid waits for a verdict before being asked about again.</summary>
        private const float RetrySeconds = 5f;

        /// <summary>How often the corpses already standing are looked for.</summary>
        private const float SweepSeconds = 5f;

        /// <summary>
        /// How close a corpse must be to be asked about. Farther ones are corrected as they stream in, and an
        /// unbounded sweep floods the rebuild queue.
        /// </summary>
        private const float SweepRange = 600f;

        // Everything asked about and not yet answered, with when it was asked.
        private static readonly Dictionary<string, float> Pending = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Answered = new(StringComparer.Ordinal);
        private static readonly List<string> Batch = new();
        private static readonly List<string> Stale = new();

        private static float _sinceSweep;

        /// <summary>How many creatures the host has been asked about, for <c>coop_status</c>.</summary>
        internal static int Noticed { get; private set; }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // Both: LazyActor.IsDead wraps IsActorAlive and either can be inlined away, see Fired.
            Hooking.TryPostfix(harmony, typeof(SavegameDataExtensions), nameof(SavegameDataExtensions.IsActorAlive),
                typeof(HostWorldState), nameof(IsActorAlivePostfix));

            Hooking.TryPostfix(harmony, typeof(Il2CppDrova.Utilities.LazyLoading.LazyActor), "IsDead",
                typeof(HostWorldState), nameof(IsDeadPostfix));
        }

        /// <summary>Which of the two hooks has actually run, for <c>coop_status</c>.</summary>
        internal static string Fired =>
            (_aliveFired ? "IsActorAlive" : "-") + " / " + (_deadFired ? "LazyActor.IsDead" : "-");

        private static bool _aliveFired;
        private static bool _deadFired;

        /// <summary>
        /// The lazy handle decides whether to build a body at all, so it sees creatures the inner method may not.
        /// </summary>
        private static void IsDeadPostfix(Il2CppDrova.Utilities.LazyLoading.LazyActor __instance, ref bool __result)
        {
            try
            {
                _deadFired = true;

                if (!__result) return;
                if (CoopSession.State != CoopSessionState.Joined) return;

                string guid = __instance.GUID;
                if (string.IsNullOrEmpty(guid)) return;

                if (EnemyRegistry.IsKnownDead(guid)) return;

                if (!Answered.Contains(guid) && Pending.TryAdd(guid, 0f))
                {
                    Noticed++;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[World] deciding whether a lazy creature is dead failed: " + e);
            }
        }

        /// <summary>Asks the host about anything still unanswered. Call once per frame on a client.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Joined)
            {
                Clear();
                return;
            }

            _sinceSweep += unscaledDeltaTime;
            if (_sinceSweep >= SweepSeconds)
            {
                _sinceSweep = 0f;
                SweepStandingCorpses();
            }

            if (Pending.Count == 0) return;

            try
            {
                Batch.Clear();
                Stale.Clear();

                foreach (KeyValuePair<string, float> waiting in Pending)
                {
                    if (waiting.Value > 0f && waiting.Value + RetrySeconds > UnityEngine.Time.unscaledTime) continue;

                    Batch.Add(waiting.Key);
                    Stale.Add(waiting.Key);

                    if (Batch.Count >= BatchSize) break;
                }

                foreach (string guid in Stale)
                {
                    Pending[guid] = UnityEngine.Time.unscaledTime;
                }

                if (Batch.Count > 0)
                {
                    CoopSession.AskAboutWorldState(Batch);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[World] asking the host what it has killed failed: " + e);
            }
        }

        /// <summary>Forgets everything. Called when a session ends or the world goes away.</summary>
        internal static void Clear()
        {
            Pending.Clear();
            Answered.Clear();
            Noticed = 0;
            _sinceSweep = 0f;
        }

        /// <summary>
        /// Queues corpses that were already built before the session started, which the hooks never see again.
        /// </summary>
        private static void SweepStandingCorpses()
        {
            try
            {
                if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

                Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppDrova.Utilities.LazyLoading.LazyActor>
                    handles = entities._lazyActorGuidDictionary;
                if (handles == null) return;

                if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

                UnityEngine.Vector2 origin = player.transform.position;
                const float squared = SweepRange * SweepRange;

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string,
                    Il2CppDrova.Utilities.LazyLoading.LazyActor> pair in handles)
                {
                    string guid = pair.Key;

                    // Local set lookups first, before anything crosses the interop boundary.
                    if (string.IsNullOrEmpty(guid)) continue;
                    if (EnemyRegistry.IsKnownDead(guid)) continue;

                    // Minted here, so the host has no record, and no record reads as alive.
                    if (ActorSpawnAccess.IsRuntimeCreated(guid)) continue;

                    Il2CppDrova.Utilities.LazyLoading.LazyActor lazy = pair.Value;
                    if (lazy == null) continue;

                    if (((UnityEngine.Vector2)lazy.Position - origin).sqrMagnitude > squared) continue;

                    if (!lazy.IsDead()) continue;

                    // Called alive but still lying here, so the rebuild was lost. Retry only while off screen,
                    // since a rebuild visibly swaps the corpse for a live creature.
                    if (EnemyRegistry.IsKnownAlive(guid))
                    {
                        Actor corpse = entities.GetActorFromGuid(guid);

                        if (corpse == null || !corpse.IsVisible)
                        {
                            EnemyRebirth.Request(guid);
                        }

                        continue;
                    }

                    if (!Answered.Contains(guid) && Pending.TryAdd(guid, 0f))
                    {
                        Noticed++;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[World] looking for corpses the host was never asked about failed: " + e);
            }
        }

        /// <summary>Records the host's verdict on a creature.</summary>
        /// <summary>A savegame was loaded: ask about every creature again.</summary>
        internal static void ForgetAnswered() => Answered.Clear();

        internal static void RecordAnswered(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            Pending.Remove(guid);
            Answered.Add(guid);
        }

        /// <summary>Queues a creature this save calls dead, without changing the answer.</summary>
        private static void IsActorAlivePostfix(string actorGuid, ref bool __result)
        {
            try
            {
                _aliveFired = true;

                // No save record also reads as alive.
                if (__result) return;

                if (CoopSession.State != CoopSessionState.Joined) return;
                if (string.IsNullOrEmpty(actorGuid)) return;

                if (EnemyRegistry.IsKnownDead(actorGuid)) return;

                if (!Answered.Contains(actorGuid) && Pending.TryAdd(actorGuid, 0f))
                {
                    Noticed++;
                }
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside save loading, and a throw would lose a whole chunk of the world.
                MelonLogger.Error("[World] deciding whether a creature is alive failed: " + e);
            }
        }
    }
}
