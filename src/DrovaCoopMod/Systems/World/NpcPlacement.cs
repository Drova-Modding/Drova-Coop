using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Puts authored NPCs where the host's world says they stand, since unshared quest staging can leave them far apart.
    /// A far-off copy is moved with the game's set-default-position recipe and its routine gated off.
    /// </summary>
    internal sealed class NpcPlacement : ICoopSystem
    {
        /// <summary>How often nearby actors are gathered for asking.</summary>
        private const float SweepSeconds = 5f;

        /// <summary>How long before the same actor is asked about again.</summary>
        private const float AskRetrySeconds = 30f;

        /// <summary>Disagreement below this stays local, since routine walking drifts copies tens of units apart.</summary>
        private const float ReconcileDistance = 400f;

        /// <summary>How often suppressed guids are re-resolved to live bodies for the routine gate.</summary>
        private const float RefreshSeconds = 1f;

        /// <summary>When each guid was last asked about, as a reading of <see cref="_now"/>.</summary>
        private static readonly Dictionary<string, float> AskedAgo = new(StringComparer.Ordinal);

        private static readonly Dictionary<string, Vector2> AskedWhere = new(StringComparer.Ordinal);
        private static readonly HashSet<string> Suppressed = new(StringComparer.Ordinal);
        private static readonly HashSet<IntPtr> SuppressedBodies = new();
        private static readonly List<string> Batch = new();

        private static float _sinceSweep;
        private static float _sinceRefresh;
        private static float _now;

        private static readonly Queue<NpcPlacementAnswerMessage> IncomingNpcPlacements = new();

        /// <summary>How many actors were moved to the host's placement. For <c>coop_status</c>.</summary>
        internal static int Reconciled { get; private set; }

        /// <summary>
        /// How often the routine gate suppressed a place this session. Zero with <see cref="Reconciled"/> above zero means the gate never fires.
        /// </summary>
        internal static int GateAnswered { get; private set; }

        /// <summary>How many times a gated actor was let through to a quest leg. For <c>coop_status</c>.</summary>
        internal static int QuestLegsLetThrough { get; private set; }

        /// <summary>How many actors have their routine gated off right now. For <c>coop_status</c>.</summary>
        internal static int Holding => Suppressed.Count;

        /// <summary>How many gated actors were let go because they left the world.</summary>
        internal static int Released { get; private set; }

        /// <summary>Whether the unload release installed. Without it the gate can outlive the body.</summary>
        internal static bool UnloadHooked { get; private set; }

        /// <summary>Forgets everything and releases every routine. Called when a session ends.</summary>
        private static void Reset()
        {
            AskedAgo.Clear();
            AskedWhere.Clear();
            Suppressed.Clear();
            SuppressedBodies.Clear();
            Batch.Clear();
            _sinceSweep = 0f;
            _sinceRefresh = 0f;
            _now = 0f;
            Reconciled = 0;
            Released = 0;
            GateAnswered = 0;
            QuestLegsLetThrough = 0;
        }

        /// <summary>Applies the routine gate.</summary>
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(Il2CppDrova.Routine.ActorRoutineModule),
                nameof(Il2CppDrova.Routine.ActorRoutineModule.GetActiveRoutinePlace),
                typeof(NpcPlacementPatches), nameof(NpcPlacementPatches.RoutinePlacePostfix));

            // On Unload, not the private async method it calls, which has one call site and may be inlined.
            UnloadHooked = HookSwitches.IsOn(HookSwitches.NpcUnload)
                && Hooking.TryPrefix(harmony,
                    typeof(Il2CppDrova.Utilities.LazyLoading.LazyActor),
                    nameof(Il2CppDrova.Utilities.LazyLoading.LazyActor.Unload),
                    typeof(NpcPlacementPatches), nameof(NpcPlacementPatches.UnloadPrefix));
        }

        /// <summary>Asks and applies. Call once per frame from a loaded world. Client only.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.JoinedWorld)
            {
                if (Suppressed.Count > 0) Reset();
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

        /// <summary>Answers one placement question from the host's lazy-actor records, without loading anything.</summary>
        private static void Answer(INetPeer peer, in NpcPlacementRequestMessage request)
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

                    Il2CppDrova.Utilities.LazyLoading.LazyActor? lazy = entities.GetLazyActorFromGuid(guid);

                    if (lazy != null && !lazy.IsDestroyed)
                    {
                        record.Flags = NpcPlacementAnswerMessage.FlagKnown;

                        if (lazy.IsDead())
                        {
                            record.Flags |= NpcPlacementAnswerMessage.FlagDead;
                        }

                        Vector2 position = lazy._runtimeActor != null
                            ? lazy._runtimeActor.GetFeetPosition()
                            : lazy.Position;

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

                Batch.Clear();

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Actor> pair
                    in entities._entityGuidDictionary)
                {
                    Actor? actor = pair.Value;
                    if (actor == null || actor.IsDestroyed || !actor.IsInitialized) continue;

                    string guid = pair.Key;
                    if (string.IsNullOrEmpty(guid)) continue;

                    if (actor.Pointer == self.Pointer) continue;
                    if (RemotePlayers.IsPuppet(actor)) continue;

                    // Every loaded actor, not only nearby ones. One already gated, or moved far since asked, is asked every sweep.
                    Vector2 position = actor.GetFeetPosition();

                    // This machine's own game-arena event stages these actors, so it is not a disagreement.
                    if (ArenaGates.RunsTheFight() && ArenaGates.AtTheArena(position))
                    {
                        Suppressed.Remove(guid);
                        continue;
                    }

                    float retry = Suppressed.Contains(guid) ? SweepSeconds : AskRetrySeconds;
                    bool moved = AskedWhere.TryGetValue(guid, out Vector2 where)
                        && (position - where).sqrMagnitude > ReconcileDistance * ReconcileDistance;
                    if (!moved && AskedAgo.TryGetValue(guid, out float at) && _now - at < retry) continue;

                    // Only actors both worlds can name and the session is not already driving.
                    if (ActorSpawnAccess.IsRuntimeCreated(guid)) continue;
                    if (entities.GetLazyActorFromGuid(guid) == null) continue;
                    if (EnemyRegistry.IsInSessionHere(guid)) continue;

                    AskedAgo[guid] = _now;
                    AskedWhere[guid] = position;
                    Batch.Add(guid);
                }

                for (int start = 0; start < Batch.Count; start += NpcPlacementRequestMessage.MaxGuids)
                {
                    int count = Math.Min(NpcPlacementRequestMessage.MaxGuids, Batch.Count - start);

                    string[] chunk = new string[count];
                    Batch.CopyTo(start, chunk, 0, count);

                    AskNpcPlacement(new NpcPlacementRequestMessage { Guids = chunk });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Npc] gathering actors to ask about failed: " + e);
            }
        }

        private static void DrainAnswers()
        {
            while (TryTakeNpcPlacement(out NpcPlacementAnswerMessage message))
            {
                if (message.Records == null) continue;

                foreach (NpcPlacementAnswerMessage.Record record in message.Records)
                {
                    Apply(record);
                }
            }
        }

        private static bool IsPlayerConversing()
        {
            return Il2CppDrova.DialogueNew.DialogueSystemGameHandler.TryGet(out Il2CppDrova.DialogueNew.DialogueSystemGameHandler dialogues)
                && dialogues.ActivePlayerDialogue != null && dialogues.ActivePlayerDialogue.isRunning;
        }

        private static void Apply(in NpcPlacementAnswerMessage.Record record)
        {
            try
            {
                if (string.IsNullOrEmpty(record.Guid)) return;

                // Deaths are the graveyard's job.
                if ((record.Flags & NpcPlacementAnswerMessage.FlagKnown) == 0) return;
                if ((record.Flags & NpcPlacementAnswerMessage.FlagDead) != 0) return;

                if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

                Actor? actor = entities.GetActorFromGuid(record.Guid);
                if (actor == null || actor.IsDestroyed || !actor.IsInitialized) return;

                // Not during a dialogue, whose cutscene nodes stage NPCs before they count as talking. Asked again soon.
                if ((actor.DialogueModule != null && actor.DialogueModule.IsInDialogue()) || IsPlayerConversing())
                {
                    AskedAgo[record.Guid] = _now - (AskRetrySeconds - SweepSeconds);
                    return;
                }

                Vector2 target = new(record.X, record.Y);
                Vector2 local = actor.GetFeetPosition();

                if ((target - local).sqrMagnitude <= ReconcileDistance * ReconcileDistance) return;

                // The default position moves too, or the idle graph walks the gated actor back to its origin.
                Suppressed.Add(record.Guid);
                SuppressedBodies.Add(actor.Pointer);

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
            if (Suppressed.Count == 0) return;
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            SuppressedBodies.Clear();

            foreach (string guid in Suppressed)
            {
                Actor? actor = entities.GetActorFromGuid(guid);
                if (actor == null || actor.IsDestroyed) continue;

                SuppressedBodies.Add(actor.Pointer);
            }
        }

        internal static void RoutinePlacePostfix(Il2CppDrova.Routine.ActorRoutineModule __instance,
            ref Il2CppDrova.Routine.IRoutinePlace? __result)
        {
            if (SuppressedBodies.Count == 0 || __result == null) return;

            try
            {
                Actor? actor = __instance._actor;
                if (actor == null || !SuppressedBodies.Contains(actor.Pointer)) return;

                // Never a quest leg, since arriving writes the stage variable and the host's lazy copy does not walk.
                Il2CppDrova.Routine.BaseRoutinePlace? place = __result.TryCast<Il2CppDrova.Routine.BaseRoutinePlace>();
                if (place != null && place.QuestVarService?.FinishedGVars?.Count > 0)
                {
                    QuestLegsLetThrough++;
                    return;
                }

                GateAnswered++;
                __result = null;
            }
            catch
            {
                // Fails open, since this runs for every routine in the world.
            }
        }

        /// <summary>
        /// Lets go of an actor that is leaving the world, since unloading reads the gate's null place as
        /// permission to snap a port-home actor to its saved origin.
        /// </summary>
        internal static void UnloadPrefix(Il2CppDrova.Utilities.LazyLoading.LazyActor __instance)
        {
            if (Suppressed.Count == 0) return;

            try
            {
                string guid = __instance.GUID;
                if (string.IsNullOrEmpty(guid)) return;
                if (!Suppressed.Remove(guid)) return;

                Released++;

                // Without a body the whole set goes, since a stale pointer could gate the wrong body. The refresh rebuilds it.
                Actor? body = __instance.Actor;

                if (body != null) SuppressedBodies.Remove(body.Pointer);
                else SuppressedBodies.Clear();

                MelonLogger.Msg($"[Npc] '{Head(guid)}' left the world while its routine was gated off, " +
                    "and the gate let go. A null routine place is what makes the game snap an actor to " +
                    "its saved origin on unload, and that answer must never be ours.");
            }
            catch
            {
                // Fails open, since this runs on every lazy unload, single player included.
            }
        }

        private static string Head(string guid)
        {
            return guid.Length <= 8 ? guid : guid[..8];
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<NpcPlacementRequestMessage>(MessageIds.NpcPlacementRequest, OnNpcPlacementRequest);
            CoopSession.Register<NpcPlacementAnswerMessage>(MessageIds.NpcPlacementAnswer,
                (_, m) => CoopSession.QueueFromHost(m, IncomingNpcPlacements, CoopSession.MaxPendingFromHost, "placement answers"));
        }

        void ICoopSystem.EndSession()
        {
            IncomingNpcPlacements.Clear();
        }

        /// <summary>Takes the next placement answer from the host.</summary>
        private static bool TryTakeNpcPlacement(out NpcPlacementAnswerMessage message)
            => IncomingNpcPlacements.TryDequeue(out message);

        /// <summary>Asks the host where its world has these actors. Client only.</summary>
        private static void AskNpcPlacement(in NpcPlacementRequestMessage message)
        {
            if (!CoopSession.JoinedWorld) return;

            NetworkAccess.SendToAll(message);
        }

        private static void OnNpcPlacementRequest(INetPeer peer, NpcPlacementRequestMessage message)
        {
            if (!CoopSession.HostsWorld) return;

            Answer(peer, message);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
