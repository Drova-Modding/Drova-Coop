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

        /// <summary>
        /// When each guid was last asked about, as a reading of <see cref="_now"/>.
        ///
        /// A deadline rather than an age, so the name reads slightly wrong: nothing walks this
        /// dictionary per frame any more. It used to, over every guid ever asked about, for the whole
        /// session, allocating an array each time to avoid mutating while enumerating.
        /// </summary>
        private static readonly Dictionary<string, float> _askedAgo = new(StringComparer.Ordinal);
        private static readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);
        private static readonly HashSet<IntPtr> _suppressedBodies = new();
        private static readonly List<string> _batch = new();

        private static float _sinceSweep;
        private static float _sinceRefresh;
        private static float _now;

        /// <summary>How many actors were moved to the host's placement. For <c>coop_status</c>.</summary>
        internal static int Reconciled { get; private set; }

        /// <summary>
        /// How often the routine gate suppressed a place, this session.
        ///
        /// The number this whole subsystem turns on, and it has never once been observed non-zero -
        /// the only status readout in the last session's logs was taken before the first reconcile.
        /// Zero here with <see cref="Reconciled"/> above zero would mean the postfix never reaches the
        /// AI's interface-dispatched calls at all, and that whatever the gate is thought to be doing,
        /// it is not doing it.
        /// </summary>
        internal static int GateAnswered { get; private set; }

        /// <summary>How many times a gated actor was let through to a quest leg. For <c>coop_status</c>.</summary>
        internal static int QuestLegsLetThrough { get; private set; }

        /// <summary>How many actors have their routine gated off right now. For <c>coop_status</c>.</summary>
        internal static int Holding => _suppressed.Count;

        /// <summary>How many gated actors were let go because they left the world.</summary>
        internal static int Released { get; private set; }

        /// <summary>Whether the unload release installed. Without it the gate can outlive the body.</summary>
        internal static bool UnloadHooked { get; private set; }

        /// <summary>Forgets everything and releases every routine. Called when a session ends.</summary>
        internal static void Reset()
        {
            _askedAgo.Clear();
            _suppressed.Clear();
            _suppressedBodies.Clear();
            _batch.Clear();
            _sinceSweep = 0f;
            _sinceRefresh = 0f;
            _now = 0f;
            Reconciled = 0;
            Released = 0;

            // Cleared here, which it was not before. Without this the gate count is a total across
            // every session since the game started, and the one question it exists to answer - "did
            // the gate fire in THIS session" - cannot be asked.
            GateAnswered = 0;
            QuestLegsLetThrough = 0;
        }

        /// <summary>Applies the routine gate. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(Il2CppDrova.Routine.ActorRoutineModule),
                nameof(Il2CppDrova.Routine.ActorRoutineModule.GetActiveRoutinePlace),
                typeof(NpcPlacement), nameof(RoutinePlacePostfix));

            // Letting go when the body leaves the world. The game asks the routine module whether an
            // actor still has a place as part of unloading it, and an actor authored to port home
            // takes a null answer as permission to snap to its saved origin - so the gate must never
            // be the thing answering that question. Patched on Unload rather than on the private
            // async method it calls, which has exactly one call site and is the classic inlining
            // victim; Unload is an override, so its zero direct callers are the vtable case.
            UnloadHooked = HookSwitches.IsOn(HookSwitches.NpcUnload)
                && Hooking.TryPrefix(harmony,
                    typeof(Il2CppDrova.Utilities.LazyLoading.LazyActor),
                    nameof(Il2CppDrova.Utilities.LazyLoading.LazyActor.Unload),
                    typeof(NpcPlacement), nameof(UnloadPrefix));
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

            _now += unscaledDeltaTime;

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
                if (!PlayerAccess.TryGetPlayer(out Actor self)) return;
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

                    if (_askedAgo.TryGetValue(guid, out float at) && _now - at < AskRetrySeconds) continue;

                    _askedAgo[guid] = _now;
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
                    _askedAgo[record.Guid] = _now - (AskRetrySeconds - SweepSeconds);
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

                Reconciled++;

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

                // Never a quest leg. Arriving at one is what writes the stage's "reached" variable,
                // and only a machine running the routine can arrive: the host's copy of a far-away NPC
                // is lazy and does not walk. Gated here too, nobody ever arrives and the escort, the
                // expedition or the mine day waits for the host to come within reach.
                Il2CppDrova.Routine.BaseRoutinePlace place = __result.TryCast<Il2CppDrova.Routine.BaseRoutinePlace>();
                if (place != null && place.QuestVarService?.FinishedGVars?.Count > 0)
                {
                    QuestLegsLetThrough++;
                    return;
                }

                GateAnswered++;
                __result = null!;
            }
            catch
            {
                // A broken gate must fail open: the worst outcome of not suppressing is a walking
                // NPC, the worst outcome of throwing here is every routine in the world breaking.
            }
        }

        /// <summary>
        /// Lets go of an actor that is leaving the world.
        ///
        /// Unloading asks the routine module whether the actor still has a place, and an actor
        /// authored to port home reads a null answer as permission to snap to its saved origin. This
        /// gate answers null. So for as long as it holds an unloading actor, it is not merely
        /// suppressing a routine - it is telling the game to teleport that NPC home, every time its
        /// chunk cycles, which is the shape of an NPC that keeps re-diverging by hundreds of units.
        ///
        /// This does change what the gate does: an actor released here is not gated again until the
        /// next answer arrives, up to half a minute later. That is accepted deliberately, because the
        /// failure direction of releasing is an NPC walking its own routine, which is the direction
        /// everything else in this file already fails in.
        /// </summary>
        private static void UnloadPrefix(Il2CppDrova.Utilities.LazyLoading.LazyActor __instance)
        {
            if (_suppressed.Count == 0) return;

            try
            {
                string guid = __instance.GUID;
                if (string.IsNullOrEmpty(guid)) return;
                if (!_suppressed.Remove(guid)) return;

                Released++;

                // The body's own pointer where there is one. Where there is not, the whole set goes: a
                // raw pointer for an actor that is already gone is the one thing in this file that
                // could gate the wrong body, and the refresh rebuilds the set from _suppressed within
                // the second anyway.
                Actor body = __instance.Actor;

                if (body != null) _suppressedBodies.Remove(body.Pointer);
                else _suppressedBodies.Clear();

                MelonLogger.Msg($"[Npc] '{Head(guid)}' left the world while its routine was gated off, " +
                    "and the gate let go. A null routine place is what makes the game snap an actor to " +
                    "its saved origin on unload, and that answer must never be ours.");
            }
            catch
            {
                // Fails open, and it has to: this now sits on every lazy unload in the world, in
                // single player as much as in a session. The worst outcome of releasing is a walking
                // NPC; the worst outcome of throwing here is every unload in the game breaking.
            }
        }

        private static string Head(string guid)
        {
            return guid.Length <= 8 ? guid : guid[..8];
        }
    }
}
