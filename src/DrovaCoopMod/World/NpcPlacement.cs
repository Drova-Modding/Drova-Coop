using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Puts authored NPCs where the host's world says they stand.
    ///
    /// Quest staging places NPCs, and staging the machines do not share - a story sequence one world
    /// finished and the other never starts - leaves the same actor kilometers apart between worlds.
    /// The client asks the host about authored actors near its player; the host answers from its
    /// lazy-actor records without loading anything. A copy standing far from the host's answer is
    /// moved there with the game's own set-default-position recipe, and its routine is suppressed so
    /// this world's own staging stops walking it back. The ordinary claim and drive machinery
    /// supersedes all of this whenever the host comes close enough to claim the actor for real.
    /// </summary>
    internal static class NpcPlacement
    {
        /// <summary>How often nearby actors are gathered for asking.</summary>
        private const float SweepSeconds = 5f;

        /// <summary>How long before the same actor is asked about again.</summary>
        private const float AskRetrySeconds = 30f;

        /// <summary>How far around the local player actors are asked about.</summary>
        private const float SweepRange = 600f;

        /// <summary>
        /// Disagreement below this stays local. Routine walking drifts copies tens of units apart,
        /// which local simulation handles fine; staging disagreement is measured in thousands.
        /// </summary>
        private const float ReconcileDistance = 400f;

        /// <summary>How often suppressed guids are re-resolved to live bodies for the routine gate.</summary>
        private const float RefreshSeconds = 1f;

        private static readonly Dictionary<string, float> _askedAgo = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);
        private static readonly HashSet<IntPtr> _suppressedBodies = new();
        private static readonly List<string> _batch = new();

        private static float _sinceSweep;
        private static float _sinceRefresh;
        private static int _reconciled;
        private static int _gateAnswered;

        /// <summary>How many actors were moved to the host's placement. For <c>coop_status</c>.</summary>
        internal static int Reconciled => _reconciled;

        /// <summary>How often the routine gate suppressed a place. For measuring the patch fires.</summary>
        internal static int GateAnswered => _gateAnswered;

        /// <summary>Forgets everything and releases every routine. Called when a session ends.</summary>
        internal static void Reset()
        {
            _askedAgo.Clear();
            _suppressed.Clear();
            _suppressedBodies.Clear();
            _batch.Clear();
            _sinceSweep = 0f;
            _sinceRefresh = 0f;
            _reconciled = 0;
        }

        /// <summary>Applies the routine gate. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(Il2CppDrova.Routine.ActorRoutineModule),
                nameof(Il2CppDrova.Routine.ActorRoutineModule.GetActiveRoutinePlace),
                typeof(NpcPlacement), nameof(RoutinePlacePostfix));
        }

        /// <summary>
        /// Asks and applies. Call once per frame from a loaded world. Client side only; the host
        /// answers where its messages arrive.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Joined)
            {
                if (_suppressed.Count > 0) Reset();
                return;
            }

            foreach (string guid in _askedAgo.Keys.ToArray())
            {
                _askedAgo[guid] += unscaledDeltaTime;
            }

            DrainAnswers();

            _sinceRefresh += unscaledDeltaTime;
            if (_sinceRefresh >= RefreshSeconds)
            {
                _sinceRefresh = 0f;
                RefreshSuppressedBodies();
            }

            _sinceSweep += unscaledDeltaTime;
            if (_sinceSweep < SweepSeconds) return;

            _sinceSweep = 0f;
            Sweep();
        }

        /// <summary>
        /// Answers one placement question from the host's records, unknown and dead included, without
        /// loading anything. Called by the session where the request arrives.
        /// </summary>
        internal static void Answer(INetPeer peer, in NpcPlacementRequestMessage request)
        {
            try
            {
                if (request.Guids == null || request.Guids.Length == 0) return;
                if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

                NpcPlacementAnswerMessage.Record[] records =
                    new NpcPlacementAnswerMessage.Record[request.Guids.Length];

                for (int index = 0; index < request.Guids.Length; index++)
                {
                    string guid = request.Guids[index] ?? string.Empty;

                    NpcPlacementAnswerMessage.Record record = new() { Guid = guid };

                    Il2CppDrova.Utilities.LazyLoading.LazyActor lazy = entities.GetLazyActorFromGuid(guid);

                    if (lazy != null && !lazy.IsDestroyed)
                    {
                        record.Flags = NpcPlacementAnswerMessage.FlagKnown;

                        if (lazy.IsDead())
                        {
                            record.Flags |= NpcPlacementAnswerMessage.FlagDead;
                        }

                        Vector2 position = lazy._runtimeActor != null
                            ? (Vector2)lazy._runtimeActor.GetFeetPosition()
                            : (Vector2)lazy.Position;

                        record.X = position.x;
                        record.Y = position.y;
                    }

                    records[index] = record;
                }

                NetworkAccess.Send(peer, new NpcPlacementAnswerMessage { Records = records });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Npc] answering a placement question failed: " + e);
            }
        }

        private static void Sweep()
        {
            try
            {
                if (!PlayerAccess.TryGetPlayer(out Actor self) || self == null) return;
                if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

                Vector2 origin = self.GetFeetPosition();
                float squared = SweepRange * SweepRange;

                _batch.Clear();

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Actor> pair
                    in entities._entityGuidDictionary)
                {
                    Actor actor = pair.Value;
                    if (actor == null || actor.IsDestroyed || !actor.IsInitialized) continue;

                    string guid = pair.Key;
                    if (string.IsNullOrEmpty(guid)) continue;

                    if (actor.Pointer == self.Pointer) continue;
                    if (RemotePlayers.IsPuppet(actor)) continue;

                    // Only actors both worlds can name, and only ones the session is not already
                    // driving - a bound actor's placement is the host's live stream.
                    if (ActorSpawnAccess.IsRuntimeCreated(guid)) continue;
                    if (entities.GetLazyActorFromGuid(guid) == null) continue;
                    if (EnemyRegistry.IsInSessionHere(guid)) continue;

                    if (((Vector2)actor.GetFeetPosition() - origin).sqrMagnitude > squared) continue;

                    if (_askedAgo.TryGetValue(guid, out float ago) && ago < AskRetrySeconds) continue;

                    _askedAgo[guid] = 0f;
                    _batch.Add(guid);
                }

                for (int start = 0; start < _batch.Count; start += NpcPlacementRequestMessage.MaxGuids)
                {
                    int count = Math.Min(NpcPlacementRequestMessage.MaxGuids, _batch.Count - start);

                    string[] chunk = new string[count];
                    _batch.CopyTo(start, chunk, 0, count);

                    CoopSession.AskNpcPlacement(new NpcPlacementRequestMessage { Guids = chunk });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Npc] gathering actors to ask about failed: " + e);
            }
        }

        private static void DrainAnswers()
        {
            while (CoopSession.TryTakeNpcPlacement(out NpcPlacementAnswerMessage message))
            {
                if (message.Records == null) continue;

                foreach (NpcPlacementAnswerMessage.Record record in message.Records)
                {
                    Apply(record);
                }
            }
        }

        private static void Apply(in NpcPlacementAnswerMessage.Record record)
        {
            try
            {
                if (string.IsNullOrEmpty(record.Guid)) return;

                // Unknown to the host's world, or dead in it: placement is not this system's to
                // decide. Deaths are the graveyard's job.
                if ((record.Flags & NpcPlacementAnswerMessage.FlagKnown) == 0) return;
                if ((record.Flags & NpcPlacementAnswerMessage.FlagDead) != 0) return;

                if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

                Actor actor = entities.GetActorFromGuid(record.Guid);
                if (actor == null || actor.IsDestroyed || !actor.IsInitialized) return;

                // Somebody is talking to this NPC right now. Moving a dialogue partner mid-sentence
                // is worse than a late reconcile; asked again sooner than the ordinary retry.
                if (actor.DialogueModule != null && actor.DialogueModule.IsInDialogue())
                {
                    _askedAgo[record.Guid] = AskRetrySeconds - SweepSeconds;
                    return;
                }

                Vector2 target = new(record.X, record.Y);
                Vector2 local = actor.GetFeetPosition();

                if ((target - local).sqrMagnitude <= ReconcileDistance * ReconcileDistance) return;

                // The game's own put-him-there recipe. The default position must move too: with the
                // routine gated off, the idle graph walks an actor back to its saved origin, and a
                // bare teleport would be undone by that fallback.
                _suppressed.Add(record.Guid);
                _suppressedBodies.Add(actor.Pointer);

                Il2CppDrova.Stats.ICustomStats stats = actor.CustomStats;
                if (stats != null)
                {
                    stats.SetNewDefaultPosition(target, Vector2.right);
                    stats.RequestSetNewDefaultPos = true;
                    stats.RequestDefaultRoutinePosChange = true;
                }

                actor.TeleportTo(target, portWithGroup: false, updateCam: false);

                _reconciled++;

                MelonLogger.Msg($"[Npc] '{actor.name}' stood {(target - local).magnitude:0} units from " +
                    "where the host's world has it, and was moved there. Its routine here is gated off, " +
                    "because this world's staging disagrees and would walk it back.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Npc] applying a placement failed: " + e);
            }
        }

        private static void RefreshSuppressedBodies()
        {
            if (_suppressed.Count == 0) return;
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            _suppressedBodies.Clear();

            foreach (string guid in _suppressed)
            {
                Actor actor = entities.GetActorFromGuid(guid);
                if (actor == null || actor.IsDestroyed) continue;

                _suppressedBodies.Add(actor.Pointer);
            }
        }

        private static void RoutinePlacePostfix(Il2CppDrova.Routine.ActorRoutineModule __instance,
            ref Il2CppDrova.Routine.IRoutinePlace __result)
        {
            if (_suppressedBodies.Count == 0 || __result == null) return;

            try
            {
                Actor actor = __instance._actor?.TryCast<Actor>();
                if (actor == null || !_suppressedBodies.Contains(actor.Pointer)) return;

                _gateAnswered++;
                __result = null!;
            }
            catch
            {
                // A broken gate must fail open: the worst outcome of not suppressing is a walking
                // NPC, the worst outcome of throwing here is every routine in the world breaking.
            }
        }
    }
}
