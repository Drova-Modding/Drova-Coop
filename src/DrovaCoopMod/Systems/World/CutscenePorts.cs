using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using Il2CppDrova.Cutscenes;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Replays a cutscene's NPC ports and walks in every world, since the cutscene itself runs only on the talker's machine.
    /// Players are never ported from here; <see cref="GateFollow"/> brings them along.
    /// </summary>
    internal sealed class CutscenePorts : ICoopSystem
    {
        /// <summary>How long after a teammate's port a cutscene walk here on the same NPC is taken as stale.</summary>
        private const float RecentPortSeconds = 10f;

        /// <summary>The longest walk a teammate's scene is replayed for: scene walks are steps, not trips between floors.</summary>
        private const float MaxWalkDistance = 1500f;

        /// <summary>How near this player a ported NPC is faded rather than popped: about a screen.</summary>
        private const float SeenRange = 300f;

        /// <summary>The game's own scene teleport fade length.</summary>
        private const float FadeSeconds = 0.3f;

        private static readonly Queue<ActorPortMessage> IncomingActorPorts = new();
        private static readonly Dictionary<IntPtr, float> PortedAt = new();
        private static readonly List<(Actor Actor, ActorPortMessage Message, float Left)> Fading = new();

        /// <summary>How many ports this machine's cutscenes told the session. For the log.</summary>
        private static int Sent { get; set; }

        /// <summary>How many ports a teammate's cutscene made here. For <c>coop_status</c> and tests.</summary>
        internal static int Applied { get; private set; }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        private static void Reset()
        {
            Sent = 0;
            Applied = 0;
            PortedAt.Clear();
            Fading.Clear();
        }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(CS_Action_TeleportActor), nameof(CS_Action_TeleportActor.StartAction),
                typeof(CutscenePortsPatches), nameof(CutscenePortsPatches.TeleportPostfix));
            Hooking.TryPostfix(harmony, typeof(CS_Action_PortActorToRoutines), nameof(CS_Action_PortActorToRoutines.StartAction),
                typeof(CutscenePortsPatches), nameof(CutscenePortsPatches.RoutinePostfix));
        }

        internal static void TeleportPostfix(CS_Action_TeleportActor __instance)
        {
            try
            {
                if (!CoopSession.IsLive || RemoteApply<CutsceneScope>.InProgress) return;

                string? guid = GuidOf(__instance.TargetEntityInfo);

                // GateFollow dates a trip from the cutscene's start, so a port of the player restamps it.
                // Only the player's, or a door after an NPC port would look like a story port.
                if (guid != null && Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Actor player)
                    && guid == GuidOf(player.GetEntityInfo()))
                {
                    CutsceneWatch.Restamp();
                }

                if (guid == null) return;

                Vector2 at = __instance.Position;
                Send(new ActorPortMessage
                {
                    Kind = ActorPortMessage.KindTeleport,
                    Guid = guid,
                    X = at.x,
                    Y = at.y,
                    ResetRoutine = __instance._resetRoutine,
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cutscene] telling a cutscene teleport failed: " + e);
            }
        }

        internal static void RoutinePostfix(CS_Action_PortActorToRoutines __instance)
        {
            try
            {
                if (!CoopSession.IsLive || RemoteApply<CutsceneScope>.InProgress) return;

                if (__instance._portAll)
                {
                    Send(new ActorPortMessage { Kind = ActorPortMessage.KindRoutine, Guid = string.Empty });
                    return;
                }

                if (__instance._actorsToPort == null) return;

                foreach (EntityInfo info in __instance._actorsToPort)
                {
                    string? guid = GuidOf(info);
                    if (guid != null) Send(new ActorPortMessage { Kind = ActorPortMessage.KindRoutine, Guid = guid });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cutscene] telling a cutscene port to routines failed: " + e);
            }
        }

        /// <summary>
        /// Ends a cutscene walk that can never arrive, because the host drives the NPC or a teammate's scene already ported it.
        /// Nobody is moved.
        /// </summary>
        /// <returns>True when the walk was settled here.</returns>
        internal static bool SettleDrivenWalk(CS_Action_Move move)
        {
            Actor? actor = move._activeActor;

            // Ported on by a teammate's copy of this scene moments ago, so the mark this walk heads for is behind it.
            if (actor != null && PortedAt.TryGetValue(actor.Pointer, out float at) && Time.unscaledTime - at < RecentPortSeconds)
            {
                move._timer?.SetFinished();
                return true;
            }

            if (CoopSession.State != CoopSessionState.Joined) return false;

            if (actor == null || !EnemyRegistry.TryGetId(actor, out EnemyNetId id) || !EnemyRegistry.IsDriven(id)) return false;

            move._timer?.SetFinished();
            return true;
        }

        /// <summary>
        /// Tells the session a cutscene here walks an NPC somewhere, unless a teammate's port has already overtaken it.
        /// </summary>
        internal static void ReportWalk(CS_Action_Move move)
        {
            try
            {
                Actor? actor = move._activeActor;
                if (actor == null || RemotePlayers.IsPuppet(actor)) return;
                if (Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Actor self) && self.Pointer == actor.Pointer) return;
                if (PortedAt.TryGetValue(actor.Pointer, out float at) && Time.unscaledTime - at < RecentPortSeconds) return;

                string? guid = GuidOf(actor.GetEntityInfo());
                if (guid == null) return;

                Vector2 target = move.Position;
                ReportActorPort(new ActorPortMessage
                {
                    Kind = move._moveMode == Actor.EMoveMode.Run ? ActorPortMessage.KindRun : ActorPortMessage.KindWalk,
                    Guid = guid,
                    X = target.x,
                    Y = target.y,
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cutscene] telling a cutscene walk failed: " + e);
            }
        }

        private static void Send(in ActorPortMessage message)
        {
            Sent++;
            ReportActorPort(message);
            MelonLogger.Msg($"[Cutscene] a cutscene here ported {(message.Guid.Length == 0 ? "every actor" : message.Guid)} " +
                (message.Kind == ActorPortMessage.KindTeleport ? $"to {message.X:0},{message.Y:0}" : "to its routine place") +
                "; told to the session.");
        }

        /// <summary>Applies what teammates' cutscenes ported.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive) return;

            for (int index = Fading.Count - 1; index >= 0; index--)
            {
                (Actor actor, ActorPortMessage message, float left) = Fading[index];
                left -= Time.unscaledDeltaTime;
                if (left > 0f)
                {
                    Fading[index] = (actor, message, left);
                    continue;
                }

                Fading.RemoveAt(index);
                try
                {
                    if (actor == null || actor.IsDestroyed) continue;
                    Port(actor, message);
                    ActorFader.Begin(actor, 0f, 1f, Color.black, Color.clear, FadeSeconds, destroyAtEnd: true);
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[Cutscene] finishing a faded port failed: " + e);
                }
            }

            while (TryTakeActorPort(out ActorPortMessage message))
            {
                try
                {
                    using (RemoteApply<CutsceneScope>.Enter()) Apply(message);
                    Applied++;
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[Cutscene] applying a teammate's cutscene port failed: " + e);
                }
            }
        }

        private static void Apply(in ActorPortMessage message)
        {
            if (message.Guid.Length == 0)
            {
                // What CS_Action_PortActorToRoutines does with _portAll: every lazy actor listens.
                DaytimeGameHandler? daytime = DaytimeGameHandler.TryGet();
                if (daytime != null) daytime.ForceActorToRoutinePlaceEvent.InvokeEvent();
                MelonLogger.Msg("[Cutscene] a teammate's cutscene sent every actor to its routine place; done here too.");
                return;
            }

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            LazyActor? lazy = entities.GetLazyActorFromGuid(message.Guid);
            if (lazy == null || lazy.IsDestroyed) return;

            Actor? actor = lazy._runtimeActor;
            if (message.Kind is ActorPortMessage.KindWalk or ActorPortMessage.KindRun)
            {
                // The scene's own recipe (CS_Action_Move.StartAction). A copy the host drives follows the host's stream instead.
                if (actor == null || actor.IsDestroyed || !actor.IsInitialized) return;
                if (EnemyRegistry.TryGetId(actor, out EnemyNetId id) && EnemyRegistry.IsDriven(id)) return;

                // A walk this far predates a port the machines played in another order.
                Vector2 walkTo = new(message.X, message.Y);
                float far = (actor.GetFeetPosition() - walkTo).magnitude;
                if (far > MaxWalkDistance)
                {
                    MelonLogger.Msg($"[Cutscene] a teammate's scene walk of '{lazy.name}' was {far:0} units off; left out.");
                    return;
                }

                Actor.MovementModule movement = actor.GetMovementModule();
                movement.SetIntendedMode(message.Kind == ActorPortMessage.KindRun ? Actor.EMoveMode.Run : Actor.EMoveMode.Walk);
                movement.SetTarget(new Il2CppSystem.Nullable<Vector2>(walkTo));
                MelonLogger.Msg($"[Cutscene] a teammate's scene walks '{lazy.name}' {far:0} units; done here too.");
                return;
            }

            if (message.Kind != ActorPortMessage.KindTeleport)
            {
                lazy.ForceActorToPortToNextRoutine();
            }
            else if (actor != null && !actor.IsDestroyed && actor.IsInitialized)
            {
                // Faded when this player can see it, as CS_Action_TeleportActor does for the player.
                if (Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Actor self)
                    && (actor.GetFeetPosition() - self.GetFeetPosition()).sqrMagnitude < SeenRange * SeenRange)
                {
                    ActorFader.Begin(actor, 1f, 0f, Color.clear, Color.black, FadeSeconds, destroyAtEnd: true);
                    Fading.Add((actor, message, FadeSeconds));
                }
                else
                {
                    Port(actor, message);
                }
            }
            else
            {
                // Unloaded: a lazy actor is its transform until it loads, so put it there exactly rather than routine-port it.
                Vector3 at = lazy.transform.position;
                lazy.transform.position = new Vector3(message.X, message.Y, at.z);
            }

            MelonLogger.Msg($"[Cutscene] a teammate's cutscene ported '{lazy.name}'; done here too.");
        }

        /// <summary>CS_Action_TeleportActor's own recipe, minus look.</summary>
        private static void Port(Actor actor, in ActorPortMessage message)
        {
            actor.TeleportTo(new Vector2(message.X, message.Y), portWithGroup: false, updateCam: false);
            actor.CustomStats.RequestDefaultRoutinePosChange = message.ResetRoutine;

            // Its old move target still points where it stood, so it would walk back through the void.
            Actor.MovementModule movement = actor.GetMovementModule();
            movement._actorFollowPathMovement?.Reset();
            movement.SetTarget(new Il2CppSystem.Nullable<Vector2>());
            movement.SetDirection(Vector2.zero);

            // A scene walk it is on now, or starts next, is behind the story.
            SceneMoveWatch.Finish(actor);
            PortedAt[actor.Pointer] = Time.unscaledTime;
        }

        private static string? GuidOf(EntityInfo? info)
        {
            if (info == null) return null;

            LazyActor? lazy = info.GetLazyActorFromInfo();
            return lazy == null || string.IsNullOrEmpty(lazy._guidstring) ? null : lazy._guidstring;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<ActorPortMessage>(MessageIds.ActorPort,
                (peer, m) => CoopSession.RelayAndQueue(peer, m, IncomingActorPorts, CoopSession.MaxPendingFromHost, "cutscene actor ports"));
        }

        void ICoopSystem.EndSession()
        {
            IncomingActorPorts.Clear();
        }

        /// <summary>Takes the next NPC port a teammate's cutscene made, if any.</summary>
        private static bool TryTakeActorPort(out ActorPortMessage message)
            => IncomingActorPorts.TryDequeue(out message);

        /// <summary>Reports a cutscene here porting an NPC. The host passes it on.</summary>
        private static void ReportActorPort(in ActorPortMessage message)
            => CoopSession.SendIfLive(message);

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
