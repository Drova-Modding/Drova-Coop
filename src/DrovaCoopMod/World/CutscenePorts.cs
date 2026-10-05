using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.Cutscenes;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes the NPC ports inside a cutscene happen in every world.
    ///
    /// A story cutscene runs only where its dialogue runs. Some of them carry NPCs into the next chapter:
    /// the Nemeton ritual sends Molvina's party to their routine places inside the Red Tower, and a
    /// far-away NPC only takes a new routine place when such a port asks it to. Run on a client alone, the
    /// host - whose world every client's NPCs are placed from - kept the party at home, and the client's
    /// copies were pulled back after it. Run on the host alone, the clients' copies stayed behind.
    ///
    /// Only the ports cross, not the cutscene: cameras, moves and lines are the talker's staging. The
    /// player is never ported from here - <see cref="GateFollow"/> brings players along.
    /// </summary>
    internal static class CutscenePorts
    {
        /// <summary>How many ports this machine's cutscenes told the session. For the log.</summary>
        internal static int Sent { get; private set; }

        /// <summary>How long after a teammate's port a cutscene walk here on the same NPC is taken as stale.</summary>
        private const float RecentPortSeconds = 10f;

        private static readonly Dictionary<IntPtr, float> _portedAt = new();

        /// <summary>The longest walk a teammate's scene is replayed for: scene walks are steps, not trips between floors.</summary>
        private const float MaxWalkDistance = 1500f;

        /// <summary>How near this player a ported NPC is faded rather than popped: about a screen.</summary>
        private const float SeenRange = 300f;

        /// <summary>The fade each way, the game's own scene teleport length.</summary>
        private const float FadeSeconds = 0.3f;

        // Ports fading out here, done when their time runs out.
        private static readonly List<(Actor Actor, ActorPortMessage Message, float Left)> _fading = new();

        /// <summary>How many ports a teammate's cutscene made here. For <c>coop_status</c> and tests.</summary>
        internal static int Applied { get; private set; }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            Sent = 0;
            Applied = 0;
            _portedAt.Clear();
            _fading.Clear();
        }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(CS_Action_TeleportActor), nameof(CS_Action_TeleportActor.StartAction),
                typeof(CutscenePorts), nameof(TeleportPostfix));
            Hooking.TryPostfix(harmony, typeof(CS_Action_PortActorToRoutines), nameof(CS_Action_PortActorToRoutines.StartAction),
                typeof(CutscenePorts), nameof(RoutinePostfix));
        }

        private static void TeleportPostfix(CS_Action_TeleportActor __instance)
        {
            try
            {
                if (!CoopSession.IsLive || RemoteApply<CutsceneScope>.InProgress) return;

                // The player has no lazy actor; its trips are GateFollow's - which date a trip from the cutscene's
                // start, so the port itself restamps it. A scene held up by a walk that never arrived (rescued after
                // 30 s) otherwise teleported its player outside that window, and the teammate stayed behind.
                CutsceneWatch.Restamp();

                string? guid = GuidOf(__instance.TargetEntityInfo);
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

        private static void RoutinePostfix(CS_Action_PortActorToRoutines __instance)
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
        /// Settles a cutscene walk on an NPC the host drives: its body here follows the host's snapshots and can never
        /// arrive, so the scene waited for its 30 s rescue and then ported the host's copy with a routine reset - the
        /// host's Asmus never reached the Red Tower gate after a client ran Molvina's floor-3 talk. The step is simply
        /// ended here. Nobody is moved: teleporting the body to the mark made people pop around in every client-run
        /// scene, and the host's copy is left to its own routine.
        /// </summary>
        /// <returns>True when the walk was settled here.</returns>
        internal static bool SettleDrivenWalk(CS_Action_Move move)
        {
            Actor? actor = move._activeActor;

            // Ported on by a teammate's copy of this scene moments ago: the mark this walk heads for is behind it. Both
            // machines play the Red Tower's floor talks; the client's carried the party up a floor, and the host's own
            // copy then walked the NPCs toward floor-1 marks from floor 2 for its whole 30 s rescue.
            if (actor != null && _portedAt.TryGetValue(actor.Pointer, out float at) && Time.unscaledTime - at < RecentPortSeconds)
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
        /// Tells the session a cutscene here walks an NPC somewhere. A conversation's scene runs on its player's machine
        /// alone, so everybody else saw its NPCs stand frozen through it - nine of them through the whole Red Tower summoning
        /// on the host - and jump when its closing port arrived. Not a walk a teammate's port has already overtaken.
        /// </summary>
        internal static void ReportWalk(CS_Action_Move move)
        {
            try
            {
                Actor? actor = move._activeActor;
                if (actor == null || RemotePlayers.IsPuppet(actor)) return;
                if (Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Actor self) && self.Pointer == actor.Pointer) return;
                if (_portedAt.TryGetValue(actor.Pointer, out float at) && Time.unscaledTime - at < RecentPortSeconds) return;

                string? guid = GuidOf(actor.GetEntityInfo());
                if (guid == null) return;

                Vector2 target = move.Position;
                CoopSession.ReportActorPort(new ActorPortMessage
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
            CoopSession.ReportActorPort(message);
            MelonLogger.Msg($"[Cutscene] a cutscene here ported {(message.Guid.Length == 0 ? "every actor" : message.Guid)} " +
                (message.Kind == ActorPortMessage.KindTeleport ? $"to {message.X:0},{message.Y:0}" : "to its routine place") +
                "; told to the session.");
        }

        /// <summary>Applies what teammates' cutscenes ported. Call once per frame from a loaded world.</summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive) return;

            for (int index = _fading.Count - 1; index >= 0; index--)
            {
                (Actor actor, ActorPortMessage message, float left) = _fading[index];
                left -= Time.unscaledDeltaTime;
                if (left > 0f)
                {
                    _fading[index] = (actor, message, left);
                    continue;
                }

                _fading.RemoveAt(index);
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

            while (CoopSession.TryTakeActorPort(out ActorPortMessage message))
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
                DaytimeGameHandler.TryGet()?.ForceActorToRoutinePlaceEvent.InvokeEvent();
                MelonLogger.Msg("[Cutscene] a teammate's cutscene sent every actor to its routine place; done here too.");
                return;
            }

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            LazyActor lazy = entities.GetLazyActorFromGuid(message.Guid);
            if (lazy == null || lazy.IsDestroyed) return;

            Actor actor = lazy._runtimeActor;
            if (message.Kind is ActorPortMessage.KindWalk or ActorPortMessage.KindRun)
            {
                // The scene's own recipe (CS_Action_Move.StartAction). Movement runs with the AI held for the talk; a copy the
                // host drives here follows the host's own stream instead.
                if (actor == null || actor.IsDestroyed || !actor.IsInitialized) return;
                if (EnemyRegistry.TryGetId(actor, out EnemyNetId id) && EnemyRegistry.IsDriven(id)) return;

                // A scene walks a few steps, never between floors. One that far is a walk from before a port the two machines
                // played in another order: replayed, it sent Molvina from Red Tower floor 2 back to her floor-1 mark, a
                // straight line through the void.
                Vector2 walkTo = new(message.X, message.Y);
                float far = ((Vector2)actor.GetFeetPosition() - walkTo).magnitude;
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
                // Faded out and in when this player can see it, as CS_Action_TeleportActor does for the player in the scene:
                // the talker sees the port behind their scene's fade, and a teammate beside the NPCs saw them pop about.
                if (Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Actor self)
                    && ((Vector2)actor.GetFeetPosition() - (Vector2)self.GetFeetPosition()).sqrMagnitude < SeenRange * SeenRange)
                {
                    ActorFader.Begin(actor, 1f, 0f, Color.clear, Color.black, FadeSeconds, destroyAtEnd: true);
                    _fading.Add((actor, message, FadeSeconds));
                }
                else
                {
                    Port(actor, message);
                }
            }
            else
            {
                // Unloaded here: a lazy actor is its transform until it loads. Put there exactly - a routine port
                // instead sent Molvina to the start of a routine whose first point is the floor-1 stairs, and
                // she walked the tower's floors on the client while the host had her upstairs.
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

            // Its walk here is behind it: the movement it was on still heads for where it stood. Molvina, ported from
            // floor 1 to floor 2 of the Red Tower by a teammate's scene, walked a straight line back to her floor-1 spot
            // through the void (no path joins the floors) - the walk-off seen in 3 of ~20 runs.
            Actor.MovementModule movement = actor.GetMovementModule();
            movement._actorFollowPathMovement?.Reset();
            movement.SetTarget(new Il2CppSystem.Nullable<Vector2>());
            movement.SetDirection(Vector2.zero);

            // A walk this machine's copy of the scene has it on is behind the story now, and so is one it starts next.
            SceneMoveWatch.Finish(actor);
            _portedAt[actor.Pointer] = Time.unscaledTime;
        }

        private static string? GuidOf(EntityInfo? info)
        {
            if (info == null) return null;

            LazyActor lazy = info.GetLazyActorFromInfo();
            return lazy == null || string.IsNullOrEmpty(lazy._guidstring) ? null : lazy._guidstring;
        }
    }
}
