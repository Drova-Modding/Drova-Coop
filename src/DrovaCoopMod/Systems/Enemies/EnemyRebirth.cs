using Drova_Modding_API;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Session;
using Il2Cpp;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Gives a creature this world killed but the host has alive a new body, by unloading and reloading its
    /// <see cref="LazyActor"/>. Clearing <c>_isDead</c> cannot undo the <c>ADeathBhvr</c> effects. The new body's
    /// killing <c>Saveable_Health.LoadCallback</c> read is skipped.
    /// </summary>
    internal sealed class EnemyRebirth : ICoopSystem
    {
        private const float StepSeconds = 0.25f;

        // LazyActor.UnloadAsync polls every 500 ms while loading, so a busy stream can take seconds.
        private const float PatienceSeconds = 15f;

        private const int MaxAttempts = 2;

        // A join beside a cleared camp asks for every corpse at once, and all at once is a visible freeze.
        private const int MaxInFlight = 4;

        private const int MaxQueued = 256;

        // The corpse's pointer tells a finished rebuild apart, since IsLoaded stays true until Unload's destroy lands.
        private sealed class Rebuild
        {
            internal IntPtr Corpse;

            internal float Waited;

            internal bool UnloadAsked;
        }

        private static readonly Dictionary<string, Rebuild> InFlight = new(StringComparer.Ordinal);
        private static readonly Queue<string> Waiting = new();
        private static readonly HashSet<string> Queued = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> Attempts = new(StringComparer.Ordinal);
        private static readonly List<string> Done = new();
        private static readonly List<string> Retry = new();

        private static float _sinceStep;
        private static bool _reportedFirst;

        /// <summary>How many creatures this world had killed and the host's world got back. For status.</summary>
        internal static int Rebuilt { get; private set; }

        /// <summary>How many were left as corpses because they could not be rebuilt.</summary>
        internal static int GivenUp { get; private set; }

        /// <summary>How many bodies came up alive because this player's save was not allowed to kill them.</summary>
        internal static int Overruled { get; private set; }

        /// <summary>Whether the save-health hook has ever run.</summary>
        internal static bool HookFired { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // The concrete override, since the generic LoadMonoState above it cannot be patched.
            Hooking.TryPrefix(harmony, typeof(Saveable_Health), nameof(Saveable_Health.LoadCallback),
                typeof(EnemyRebirthPatches), nameof(EnemyRebirthPatches.LoadCallbackPrefix));
        }

        /// <summary>Whether this creature is queued for a new body or having one built right now.</summary>
        /// <param name="guid">The creature's authored guid.</param>
        internal static bool IsWorkingOn(string guid)
        {
            return !string.IsNullOrEmpty(guid) && (InFlight.ContainsKey(guid) || Queued.Contains(guid));
        }

        /// <summary>Forgets a creature's used attempts, so a revive can ask again.</summary>
        /// <param name="guid">The creature's authored guid.</param>
        internal static void AllowAgain(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            Attempts.Remove(guid);
        }

        /// <summary>
        /// Asks for a creature the host has alive to be given a living body. Safe to call every frame.
        /// </summary>
        /// <param name="guid">The creature's authored guid.</param>
        internal static void Request(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (!CoopSession.IsLive) return;

            if (!EnemyRegistry.IsKnownAlive(guid)) return;

            if (InFlight.ContainsKey(guid) || Queued.Contains(guid)) return;

            Attempts.TryGetValue(guid, out int attempts);
            if (attempts >= MaxAttempts) return;

            if (Waiting.Count >= MaxQueued) return;

            Waiting.Enqueue(guid);
            Queued.Add(guid);
        }

        /// <summary>Moves the rebuilds along.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            if (InFlight.Count == 0 && Waiting.Count == 0) return;

            _sinceStep += unscaledDeltaTime;
            if (_sinceStep < StepSeconds) return;

            float waited = _sinceStep;
            _sinceStep = 0f;

            try
            {
                Start();
                Step(waited);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] rebuilding a creature the host still has alive failed: " + e);
            }
        }

        /// <summary>Forgets everything. Called when a session ends or the world goes away.</summary>
        internal static void Clear()
        {
            InFlight.Clear();
            Waiting.Clear();
            Queued.Clear();
            Attempts.Clear();
            Retry.Clear();
            _sinceStep = 0f;
        }

        private static void Start()
        {
            while (InFlight.Count < MaxInFlight && Waiting.Count > 0)
            {
                string guid = Waiting.Dequeue();
                Queued.Remove(guid);

                if (!EnemyRegistry.IsKnownAlive(guid)) continue;

                LazyActor? lazy = FindHandle(guid);

                // Not owned by the streaming system, so nothing can build a second body.
                if (lazy == null)
                {
                    GiveUp(guid);
                    continue;
                }

                Actor? body = lazy.Actor;

                Attempts.TryGetValue(guid, out int attempts);
                Attempts[guid] = attempts + 1;

                InFlight[guid] = new Rebuild
                {
                    Corpse = body != null ? body.Pointer : IntPtr.Zero,
                };
            }
        }

        private static void Step(float waited)
        {
            Done.Clear();

            foreach (KeyValuePair<string, Rebuild> pair in InFlight)
            {
                Rebuild rebuild = pair.Value;
                rebuild.Waited += waited;

                if (rebuild.Waited >= PatienceSeconds)
                {
                    GiveUp(pair.Key);
                    Done.Add(pair.Key);
                    continue;
                }

                LazyActor? lazy = FindHandle(pair.Key);
                if (lazy == null)
                {
                    GiveUp(pair.Key);
                    Done.Add(pair.Key);
                    continue;
                }

                if (Advance(pair.Key, lazy, rebuild))
                {
                    Done.Add(pair.Key);
                }
            }

            foreach (string guid in Done)
            {
                InFlight.Remove(guid);
            }

            // After the removal, since Request refuses a guid still in flight.
            foreach (string guid in Retry)
            {
                Request(guid);
            }

            Retry.Clear();
        }

        // True when the rebuild has finished, either way.
        private static bool Advance(string guid, LazyActor lazy, Rebuild rebuild)
        {
            if (!rebuild.UnloadAsked)
            {
                rebuild.UnloadAsked = true;

                // Returns immediately, the destroy lands later.
                lazy.Unload();
                return false;
            }

            if (lazy.IsLoading) return false;

            Actor? body = lazy.Actor;

            // Still the corpse: the unload has not landed yet.
            if (body != null && body.Pointer == rebuild.Corpse) return false;

            if (body == null)
            {
                // Does nothing if a load is already under way.
                lazy.Load();
                return false;
            }

            // The killing save read runs from ASaveRoot.Start, frames before the actor is initialized.
            if (!EnemyRegistry.IsReady(body)) return false;

            Health health = body.GetHealth();

            if (health == null) return true;

            // Dead anyway: the save read ran before the host's claim arrived. Retried.
            if (health.IsDead)
            {
                Attempts.TryGetValue(guid, out int attempts);

                if (attempts >= MaxAttempts)
                {
                    GiveUp(guid);
                    return true;
                }

                Retry.Add(guid);
                return true;
            }

            Rebuilt++;

            if (!_reportedFirst)
            {
                _reportedFirst = true;

                MelonLogger.Msg("[Enemy] a creature this world had already killed was given a new body, because " +
                    "the host's world still has it standing. This is what joining somebody else's world means.");
            }

            return true;
        }

        private static void GiveUp(string guid)
        {
            GivenUp++;

            Attempts[guid] = MaxAttempts;
        }

        private static LazyActor? FindHandle(string guid)
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return null;

            return entities.GetLazyActorFromGuid(guid);
        }

        /// <summary>Skips this save's killing health read for a creature the host has alive. Guards ordered by cost.</summary>
        internal static bool LoadCallbackPrefix(Saveable_Health __instance)
        {
            try
            {
                HookFired = true;

                if (!CoopSession.IsLive) return true;
                if (!EnemyRegistry.HasAnyAlive) return true;

                string guid = GuidOf(__instance);
                if (string.IsNullOrEmpty(guid)) return true;

                if (!EnemyRegistry.IsKnownAlive(guid)) return true;

                // The save agrees it is alive, so its health is worth loading.
                if (SavegameDataExtensions.IsActorAlive(guid)) return true;

                Overruled++;

                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] deciding whether this world's save may kill a creature failed: " + e);
                return true;
            }
        }

        private static string GuidOf(Saveable_Health saveable)
        {
            Health? health = saveable._health;
            if (health == null) return string.Empty;

            Entity? owner = health.OwnerEntity;
            Actor? actor = owner == null ? null : owner.TryCast<Actor>();

            return actor == null ? string.Empty : EnemyRegistry.GuidOf(actor);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Clear();
        }
    }
}
