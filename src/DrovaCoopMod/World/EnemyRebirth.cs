using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2Cpp;
using Il2CppDrova;
using Il2CppDrova.Saveables;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Gives a creature this world has already killed a living body again, by having the game build one
    /// rather than by undoing the death.
    ///
    /// Death in Drova is not a flag, and trying to clear it as though it were is what produced a corpse
    /// that walked. <c>Health._isDead</c> is only the switch; what a player sees is a chain of
    /// <c>ADeathBhvr</c> components that each listen to <c>DiedEvent</c> and act once:
    /// <c>DeathBhvr_Animation</c> parks the animator in the <c>Death</c> state and sets <c>DeathStatic</c>,
    /// <c>DeathBhvr_Color</c> tints every sprite renderer grey, <c>DeathBhvr_Physics</c> deactivates the
    /// health object, moves the body onto the <c>ObstacleFamilyOnly</c> layer, builds a polygon corpse
    /// collider and switches off every hit receiver, <c>DeathBhvr_DisableShadow</c> and
    /// <c>DeathBhvr_DisableLight</c> take the shadow and the light away, and
    /// <c>DeathBhvr_FadeAnimation</c> fades the sprites out and for some creatures destroys the actor
    /// outright. Clearing the flag and re-enabling the colliders - which is all the first attempt at this
    /// did - leaves every one of those in place, so the creature the host is driving arrives here as a grey,
    /// frozen death frame sliding across the ground.
    ///
    /// So the body is replaced instead. Every creature the session can name is one the streaming system
    /// owns: <see cref="LazyActor"/> instantiates it from an asset, stamps the authored guid onto it and
    /// registers it, and <c>Unload</c> destroys it again. Asking for that cycle hands back a body the game
    /// itself constructed, correct in every respect, with none of the above to reverse.
    ///
    /// The one thing that has to be stopped is the save killing it a second time. A freshly built body runs
    /// its own <c>Saveable_Health.LoadCallback</c> from <c>ASaveRoot.Start</c>, which reads this player's
    /// save and puts the creature straight back to zero health - so that call is skipped, and only for a
    /// creature the host has called alive, whether by claiming a body for it or by answering from its save
    /// for one it is nowhere near. The file on disk is not touched; what is refused is one read of it.
    ///
    /// A refusal is always recoverable. Anything this cannot rebuild - no handle for the guid, the
    /// reload came up dead anyway, the game took too long - is left as the corpse it already was, which is
    /// the state the mod behaved correctly in before any of this. The counts are in <c>coop_status</c>
    /// because a rebuild that silently never happens looks exactly like one that was never needed.
    /// </summary>
    internal static class EnemyRebirth
    {
        /// <summary>
        /// How often the rebuilds in flight are looked at.
        ///
        /// Not every frame: every step is a guid lookup into the streaming system's table per rebuild, and
        /// what is being waited for - a body destroyed and instantiated again, both asynchronous - takes far
        /// longer than a frame.
        /// </summary>
        private const float StepSeconds = 0.25f;

        /// <summary>
        /// How long one rebuild is given before the creature is left as a corpse.
        ///
        /// Generous because the wait is not this mod's: <c>LazyActor.UnloadAsync</c> polls every 500 ms
        /// while a load is in progress, and the instantiate that follows is budgeted against the game's own
        /// loading priority, so a busy stream can legitimately take seconds.
        /// </summary>
        private const float PatienceSeconds = 15f;

        /// <summary>
        /// How many times one creature is rebuilt before it is given up on.
        ///
        /// Low on purpose. A rebuild that came up dead again means something upstream disagrees about
        /// this creature, and retrying that forever is a body destroyed and instantiated every fifteen
        /// seconds for the rest of the session.
        /// </summary>
        private const int MaxAttempts = 2;

        /// <summary>
        /// How many bodies are destroyed and rebuilt at once.
        ///
        /// A player joining beside a camp they cleared in their own game asks for every corpse in it in the
        /// same frame, and each rebuild is an addressable load and an instantiate. Spread out, they cost a
        /// few seconds of creatures appearing; taken together they are a visible freeze.
        /// </summary>
        private const int MaxInFlight = 4;

        /// <summary>
        /// How many creatures may be waiting their turn.
        ///
        /// A bound creature is one near a player, so the honest ceiling is a few dozen; this is the point at
        /// which the queue itself is the problem rather than a symptom.
        /// </summary>
        private const int MaxQueued = 256;

        /// <summary>
        /// One rebuild being waited on.
        ///
        /// The corpse's pointer is what tells a finished rebuild from an unfinished one. <c>Unload</c>
        /// returns immediately and destroys the body on its own schedule, so <c>IsLoaded</c> is still true
        /// for some time afterwards and reading it alone would report the corpse as the new body.
        /// </summary>
        private sealed class Rebuild
        {
            internal IntPtr Corpse;

            internal float Waited;

            internal bool UnloadAsked;
        }

        private static readonly Dictionary<string, Rebuild> _inFlight = new(StringComparer.Ordinal);
        private static readonly Queue<string> _waiting = new();
        private static readonly HashSet<string> _queued = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
        private static readonly List<string> _done = new();
        private static readonly List<string> _retry = new();

        private static float _sinceStep;
        private static bool _reportedFirst;

        /// <summary>How many creatures this world had killed and the host's world got back. For status.</summary>
        internal static int Rebuilt { get; private set; }

        /// <summary>
        /// How many were left as corpses because they could not be rebuilt. Non-zero is not a fault, but it
        /// is the number that says a fight looks wrong on this screen and nowhere else.
        /// </summary>
        internal static int GivenUp { get; private set; }

        /// <summary>How many bodies came up alive because this player's save was not allowed to kill them.</summary>
        internal static int Overruled { get; private set; }

        /// <summary>
        /// Whether the save-health hook has ever run. A patch that applies and never fires looks exactly
        /// like one that works, and without this one every rebuild comes back a corpse.
        /// </summary>
        internal static bool HookFired { get; private set; }

        /// <summary>
        /// Applies the patch. Called once at melon init.
        ///
        /// <c>Saveable_Health.LoadCallback</c> is the single place a save turns a body into a corpse - the
        /// generic <c>LoadMonoState</c> above it is a closed generic whose body IL2CPP need not emit, and
        /// the override below it is a concrete method on a non-generic type, so it is both the funnel and
        /// the only member of the pair that can be patched at all.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(Saveable_Health), nameof(Saveable_Health.LoadCallback),
                typeof(EnemyRebirth), nameof(LoadCallbackPrefix));
        }

        /// <summary>
        /// Whether this creature is queued for a new body or having one built right now.
        ///
        /// Asked so a corpse that is about to be replaced is not counted as one that never will be. From
        /// outside the two look identical, and only one of them is a problem.
        /// </summary>
        /// <param name="guid">The creature's authored guid.</param>
        internal static bool IsWorkingOn(string guid)
        {
            return !string.IsNullOrEmpty(guid) && (_inFlight.ContainsKey(guid) || _queued.Contains(guid));
        }

        /// <summary>
        /// Forgets that a creature has had its attempts, so a revive can ask again. A guid given up on
        /// stays given up until the host explicitly takes the death back.
        /// </summary>
        /// <param name="guid">The creature's authored guid.</param>
        internal static void AllowAgain(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            _attempts.Remove(guid);
        }

        /// <summary>
        /// Asks for a creature to be given a living body.
        ///
        /// Ignored for anything the host has not called alive, and for a guid that has already had its
        /// attempts. Safe to call every frame; the work is what is rationed, not the asking.
        /// </summary>
        /// <param name="guid">The creature's authored guid.</param>
        internal static void Request(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (!CoopSession.IsLive) return;

            if (!EnemyRegistry.IsKnownAlive(guid)) return;

            if (_inFlight.ContainsKey(guid) || _queued.Contains(guid)) return;

            _attempts.TryGetValue(guid, out int attempts);
            if (attempts >= MaxAttempts) return;

            if (_waiting.Count >= MaxQueued) return;

            _waiting.Enqueue(guid);
            _queued.Add(guid);
        }

        /// <summary>
        /// Moves the rebuilds along. Call once per frame from a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            if (_inFlight.Count == 0 && _waiting.Count == 0) return;

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

        /// <summary>
        /// Forgets everything. Called when a session ends or the world goes away, because the guids belong
        /// to a world that is about to stop existing.
        /// </summary>
        internal static void Clear()
        {
            _inFlight.Clear();
            _waiting.Clear();
            _queued.Clear();
            _attempts.Clear();
            _retry.Clear();
            _sinceStep = 0f;
        }

        private static void Start()
        {
            while (_inFlight.Count < MaxInFlight && _waiting.Count > 0)
            {
                string guid = _waiting.Dequeue();
                _queued.Remove(guid);

                if (!EnemyRegistry.IsKnownAlive(guid)) continue;

                LazyActor? lazy = FindHandle(guid);

                // No handle means the streaming system does not own this creature, so there is nothing that
                // can build it a second body. Nothing was destroyed; the corpse stays as it is.
                if (lazy == null)
                {
                    GiveUp(guid);
                    continue;
                }

                Actor body = lazy.Actor;

                _attempts.TryGetValue(guid, out int attempts);
                _attempts[guid] = attempts + 1;

                _inFlight[guid] = new Rebuild
                {
                    Corpse = body != null ? body.Pointer : IntPtr.Zero,
                };
            }
        }

        private static void Step(float waited)
        {
            _done.Clear();

            foreach (KeyValuePair<string, Rebuild> pair in _inFlight)
            {
                Rebuild rebuild = pair.Value;
                rebuild.Waited += waited;

                if (rebuild.Waited >= PatienceSeconds)
                {
                    GiveUp(pair.Key);
                    _done.Add(pair.Key);
                    continue;
                }

                LazyActor? lazy = FindHandle(pair.Key);
                if (lazy == null)
                {
                    GiveUp(pair.Key);
                    _done.Add(pair.Key);
                    continue;
                }

                if (Advance(pair.Key, lazy, rebuild))
                {
                    _done.Add(pair.Key);
                }
            }

            foreach (string guid in _done)
            {
                _inFlight.Remove(guid);
            }

            // Queued only after the removal above. Asking while the guid is still in flight is refused by
            // design, so a retry raised from inside the walk would be silently dropped.
            foreach (string guid in _retry)
            {
                Request(guid);
            }

            _retry.Clear();
        }

        /// <summary>
        /// Takes one rebuild one step further.
        /// </summary>
        /// <returns>True when it has finished, either way.</returns>
        private static bool Advance(string guid, LazyActor lazy, Rebuild rebuild)
        {
            if (!rebuild.UnloadAsked)
            {
                rebuild.UnloadAsked = true;

                // Destroys the corpse. Asked for once and then waited on: the call returns immediately and
                // the destroy happens after however long the handle's own load is still busy.
                lazy.Unload();
                return false;
            }

            if (lazy.IsLoading) return false;

            Actor body = lazy.Actor;

            // Still the corpse, so the unload has not landed yet. Told apart by pointer rather than by
            // IsLoaded, which stays true right up until the destroy.
            if (body != null && body.Pointer == rebuild.Corpse) return false;

            if (body == null)
            {
                // Gone, and the streaming system has not put it back on its own. Asked directly; the call
                // does nothing if a load is already under way.
                lazy.Load();
                return false;
            }

            // A new body, but not yet one worth judging. The save that would kill it is read from
            // ASaveRoot.Start, in the first frame after the body is activated, whereas the actor only calls
            // itself initialized once every module has started - several frames later. Reading the health
            // before then would count a creature as saved that is about to be killed. Patience bounds this.
            if (!EnemyRegistry.IsReady(body)) return false;

            Health health = body.GetHealth();

            // No health is not a creature waiting for something; it is one that could never have been a
            // corpse. Nothing further to do.
            if (health == null) return true;

            // A new body that came up dead anyway, which means the save read was not refused - most likely
            // because the host's claim had not arrived by the time it ran. Left to a later attempt rather
            // than treated as final.
            if (health.IsDead)
            {
                _attempts.TryGetValue(guid, out int attempts);

                if (attempts >= MaxAttempts)
                {
                    GiveUp(guid);
                    return true;
                }

                _retry.Add(guid);
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

            _attempts[guid] = MaxAttempts;
        }

        private static LazyActor? FindHandle(string guid)
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return null;

            return entities.GetLazyActorFromGuid(guid);
        }

        /// <summary>
        /// Refuses one read of this player's save, for one creature the host has alive.
        ///
        /// Runs for every body that streams in, so the guards are ordered by what they cost: two field
        /// reads before anything reaches across the interop boundary for a guid, and the save is only
        /// consulted once the session has said this creature matters.
        /// </summary>
        /// <returns>False to skip the original, which is what leaves the new body alive.</returns>
        private static bool LoadCallbackPrefix(Saveable_Health __instance)
        {
            try
            {
                HookFired = true;

                if (!CoopSession.IsLive) return true;
                if (!EnemyRegistry.HasAnyAlive) return true;

                string guid = GuidOf(__instance);
                if (string.IsNullOrEmpty(guid)) return true;

                if (!EnemyRegistry.IsKnownAlive(guid)) return true;

                // The save agrees this creature is alive, so there is nothing to overrule - and its health
                // and max health are worth loading. Only the killing read is refused.
                if (SavegameDataExtensions.IsActorAlive(guid)) return true;

                Overruled++;

                return false;
            }
            catch (Exception e)
            {
                // Swallowed and allowed through. This runs inside the game's own save loading, and an
                // exception let out of here would take the rest of the body's state with it.
                MelonLogger.Error("[Enemy] deciding whether this world's save may kill a creature failed: " + e);
                return true;
            }
        }

        private static string GuidOf(Saveable_Health saveable)
        {
            Health health = saveable._health;
            if (health == null) return string.Empty;

            Actor? actor = health.OwnerEntity?.TryCast<Actor>();
            if (actor == null) return string.Empty;

            return actor.GetGuidComponent()?.GuidString ?? string.Empty;
        }
    }
}
