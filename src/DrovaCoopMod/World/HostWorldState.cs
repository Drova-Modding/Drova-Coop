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
    /// Finds out which creatures the host's world has killed that this one has not, so they can be laid
    /// out here too.
    ///
    /// This used to try to stop the client's save from having an opinion at load time, and that could
    /// never work. A session can only be started from a world that is already loaded - the mod leaves any
    /// session the moment gameplay ends - so every creature standing around the player was decided before
    /// there was a host to ask. Overruling the read only ever reached chunks that streamed in later, and it
    /// guessed "alive" for all of them, which made every corpse in the save flicker back into a creature
    /// and then die again.
    ///
    /// What is left is the half that has somewhere to apply: asking. The two hooks notice a creature
    /// this world believes is dead, the host answers from its own save, and the answers flow into the same
    /// pipeline the session's own kills use. The opposite direction - a creature the host has killed and
    /// this world still has standing - is what the asking is really for, since the host has no body for it
    /// and can never claim it.
    ///
    /// Creatures the host does have alive are brought back rather than left lying; that lives in
    /// <see cref="EnemyRegistry"/>. The "alive" answer arrives either as a bind - the host claimed a body
    /// - or as the host's word alone, answered from its save for a corpse it is nowhere near.
    /// </summary>
    internal static class HostWorldState
    {
        /// <summary>How many guids are asked about at once.</summary>
        private const int BatchSize = WorldStateRequestMessage.MaxGuids;

        /// <summary>
        /// How long a guid waits for a verdict before being asked about again. Long enough that a busy
        /// stream does not ask twice, short enough that a lost request is not a creature nobody ever
        /// corrects.
        /// </summary>
        private const float RetrySeconds = 5f;

        /// <summary>
        /// How often the corpses already standing are looked for. The hooks only see a body as the save
        /// builds it, so this covers the rest; every pass over the handle table reads a save entry per
        /// dead creature nearby, which at seconds apart costs nothing anyone can measure.
        /// </summary>
        private const float SweepSeconds = 5f;

        /// <summary>
        /// How close a corpse has to be to this player to be worth asking about.
        ///
        /// The handle table spans every loaded area scene, which is this player's entire kill history for
        /// a region - the first unbounded sweep queued 358 corpses and flooded the rebuild queue past its
        /// cap, with the fourteen in front of the player starved behind hundreds nobody could see. What
        /// the player is looking at is the only thing a rebuild can visibly fix, so it is the only thing
        /// asked for; everything further away is corrected by the save-read overrule as it streams in.
        /// </summary>
        private const float SweepRange = 600f;

        // Everything asked about and not yet answered, with when it was asked.
        private static readonly Dictionary<string, float> _pending = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _answered = new(StringComparer.Ordinal);
        private static readonly List<string> _batch = new();
        private static readonly List<string> _stale = new();

        private static float _sinceSweep;

        /// <summary>
        /// How many creatures this world believes are dead and the host has been asked about. For
        /// <c>coop_status</c>.
        /// </summary>
        internal static int Noticed { get; private set; }

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // Both, and neither is redundant. LazyActor.IsDead is a one-line wrapper over
            // IsActorAlive, which is exactly the shape the AOT compiler inlines - so a patch on the
            // wrapper can apply and never run because the caller holds a copy of the body, and a patch on
            // the inner method can apply and never run for the same reason one level down. Patching both
            // costs nothing and means only one of them has to survive.
            //
            // Which of them actually fires is not guessable and is therefore measured; see Fired.
            Hooking.TryPostfix(harmony, typeof(SavegameDataExtensions), nameof(SavegameDataExtensions.IsActorAlive),
                typeof(HostWorldState), nameof(IsActorAlivePostfix));

            Hooking.TryPostfix(harmony, typeof(Il2CppDrova.Utilities.LazyLoading.LazyActor), "IsDead",
                typeof(HostWorldState), nameof(IsDeadPostfix));
        }

        /// <summary>
        /// Which of the two hooks has actually run, for <c>coop_status</c>. A patch that applies and never
        /// fires looks exactly like one that works, so this is the only thing that tells them apart.
        /// </summary>
        internal static string Fired =>
            (_aliveFired ? "IsActorAlive" : "-") + " / " + (_deadFired ? "LazyActor.IsDead" : "-");

        private static bool _aliveFired;
        private static bool _deadFired;

        /// <summary>
        /// The same noticing, one level up. A lazy handle is where the decision not to build a body at all
        /// is taken, so it sees creatures the inner method may never be asked about.
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

                if (!_answered.Contains(guid) && _pending.TryAdd(guid, 0f))
                {
                    Noticed++;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[World] deciding whether a lazy creature is dead failed: " + e);
            }
        }

        /// <summary>
        /// Asks the host about anything still unanswered. Call once per frame on a client.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
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

            if (_pending.Count == 0) return;

            try
            {
                _batch.Clear();
                _stale.Clear();

                foreach (KeyValuePair<string, float> waiting in _pending)
                {
                    if (waiting.Value > 0f && waiting.Value + RetrySeconds > UnityEngine.Time.unscaledTime) continue;

                    _batch.Add(waiting.Key);
                    _stale.Add(waiting.Key);

                    if (_batch.Count >= BatchSize) break;
                }

                foreach (string guid in _stale)
                {
                    _pending[guid] = UnityEngine.Time.unscaledTime;
                }

                if (_batch.Count > 0)
                {
                    CoopSession.AskAboutWorldState(_batch);
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
            _pending.Clear();
            _answered.Clear();
            Noticed = 0;
            _sinceSweep = 0f;
        }

        /// <summary>
        /// Notices the corpses that were already standing when the session started.
        ///
        /// The two hooks only run as the save builds a body, and a session can only be started from a
        /// world that is already loaded - so a player who joins while standing in a camp they cleared is
        /// surrounded by corpses the hooks have already been asked about and will never be asked about
        /// again. The handle table remembers all of them; this walks it and queues whatever is dead,
        /// unanswered and nameable on the other machine.
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

                    // Cheapest guards first: local set lookups rule out almost everything before anything
                    // crosses the interop boundary for a position or a save read.
                    if (string.IsNullOrEmpty(guid)) continue;
                    if (EnemyRegistry.IsKnownDead(guid)) continue;

                    // Minted on this machine, so the host has no record of it - and no record reads as
                    // alive, which would put a new body on a corpse only this world can even name.
                    if (ActorSpawnAccess.IsRuntimeCreated(guid)) continue;

                    Il2CppDrova.Utilities.LazyLoading.LazyActor lazy = pair.Value;
                    if (lazy == null) continue;

                    if (((UnityEngine.Vector2)lazy.Position - origin).sqrMagnitude > squared) continue;

                    if (!lazy.IsDead()) continue;

                    // Already called alive, still lying here: the rebuild was asked for and did not
                    // happen - dropped by a full queue, or the answer arrived while the player was
                    // elsewhere. Asking again is free and rationed inside.
                    //
                    // Not while anyone is looking at it. A rebuild destroys the corpse and builds a live
                    // creature in its place, and on screen that is a body vanishing and a monster
                    // appearing beside the player. IsVisible is the game's own renderer culling, so
                    // "off screen" here is exactly the game's idea of it; the corpse waits, and a later
                    // sweep catches it the moment the player's back is turned.
                    if (EnemyRegistry.IsKnownAlive(guid))
                    {
                        Actor corpse = entities.GetActorFromGuid(guid);

                        if (corpse == null || !corpse.IsVisible)
                        {
                            EnemyRebirth.Request(guid);
                        }

                        continue;
                    }

                    if (!_answered.Contains(guid) && _pending.TryAdd(guid, 0f))
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

        /// <summary>
        /// Records the host's verdict on a creature.
        /// </summary>
        /// <param name="guid">Which creature.</param>
        internal static void RecordAnswered(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            _pending.Remove(guid);
            _answered.Add(guid);
        }

        /// <summary>
        /// Notices that this machine's save calls a creature dead, so the host can be asked about it.
        ///
        /// Changes nothing about the answer. The point is the noticing: a guid seen here is one worth
        /// comparing against the host's world, and the comparison is what settles which of the two is
        /// standing in a world the other cannot see.
        /// </summary>
        private static void IsActorAlivePostfix(string actorGuid, ref bool __result)
        {
            try
            {
                _aliveFired = true;

                // Alive already: the save and the host agree, or the save has no record, which the game
                // itself reads as alive.
                if (__result) return;

                if (CoopSession.State != CoopSessionState.Joined) return;
                if (string.IsNullOrEmpty(actorGuid)) return;

                // The host has said this one is dead. Its own save agrees, so nothing to overrule.
                if (EnemyRegistry.IsKnownDead(actorGuid)) return;

                // Noticed, not overruled. What this machine's save says stands - there is no body yet to
                // correct and guessing "alive" for every corpse in the save produced more churn than it
                // fixed. The guid is queued so the host can be asked about it, and the answer is applied
                // where there is something to apply it to.
                if (!_answered.Contains(actorGuid) && _pending.TryAdd(actorGuid, 0f))
                {
                    Noticed++;
                }
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside the game's own save loading, and an exception let out would
                // take a whole chunk of the world with it.
                MelonLogger.Error("[World] deciding whether a creature is alive failed: " + e);
            }
        }
    }
}
