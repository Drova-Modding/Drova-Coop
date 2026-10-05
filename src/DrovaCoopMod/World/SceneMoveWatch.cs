using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova.Cutscenes;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Ticks the timer the game forgot to tick, so a cutscene that waits for an actor to walk somewhere
    /// cannot wait forever.
    ///
    /// A cutscene move sets a maximum time and then loops until either the actor arrives or that timer
    /// says it has run out. The bound is the author's own and it is written down in the asset - but the
    /// timer it is enforced with only advances when something calls its update, the wait action ticks its
    /// own and this one ticks nothing at all. So the maximum time is dead code and the wait is unbounded:
    /// if the body never arrives, the coroutine never returns, the dialogue tree never continues, and the
    /// camera stays parked on the actor. In single player the body always arrives, so nobody ever found
    /// out.
    ///
    /// In a session it stops being hypothetical. A client staging a scene on an NPC the host is driving
    /// has that NPC's feet written from the host thirty times a second, and a body being written cannot
    /// walk anywhere - so the scene hangs until the host hears about the engagement and stands its copy
    /// down, which was measured at twenty-one seconds in one live session and read to the player as the
    /// whole game stopping. <see cref="RemoteEnemies"/> now declines to write those bodies at all, which
    /// closes that particular door; this is the belt for every other way an actor can fail to arrive.
    ///
    /// Generous on purpose - twice the authored bound, and never less than thirty seconds - because the
    /// cost of finishing a move early is a character standing in slightly the wrong place, and the cost of
    /// not finishing it is the session.
    /// </summary>
    internal static class SceneMoveWatch
    {
        /// <summary>The floor under the authored bound, before it is doubled.</summary>
        private const float MinimumBoundSeconds = 15f;

        /// <summary>How many moves may be watched at once. A cutscene runs one or two.</summary>
        private const int MaxWatched = 64;

        private static readonly Dictionary<IntPtr, (CS_Action_Move Action, float Waited, float Limit)> _live = new();

        /// <summary>Whether the watch hook is in place.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>How many cutscene moves began here. Zero during a cutscene means an inlined hook.</summary>
        internal static int Started { get; private set; }

        /// <summary>
        /// How many had to be finished by hand. Should be zero in a healthy session, and every non-zero
        /// one is a scene that would otherwise have hung until the game was killed.
        /// </summary>
        internal static int Rescued { get; private set; }

        /// <summary>How many moves are being watched right now.</summary>
        internal static int Watching => _live.Count;

        /// <summary>Whether a cutscene here is walking this body right now.</summary>
        internal static bool IsMoving(Il2CppDrova.Actor body)
        {
            foreach ((CS_Action_Move action, _, _) in _live.Values)
            {
                if (action != null && action._isMoving && action._activeActor != null
                    && action._activeActor.Pointer == body.Pointer) return true;
            }

            return false;
        }

        /// <summary>Ends a cutscene walk running here on this body, if there is one.</summary>
        internal static void Finish(Il2CppDrova.Actor body)
        {
            foreach (KeyValuePair<IntPtr, (CS_Action_Move Action, float Waited, float Limit)> pair in _live)
            {
                CS_Action_Move action = pair.Value.Action;
                if (action == null || !action._isMoving || action._activeActor == null || action._activeActor.Pointer != body.Pointer) continue;

                action._timer?.SetFinished();
                _live.Remove(pair.Key);
                return;
            }
        }

        /// <summary>Forgets what is being watched. Called when a session ends.</summary>
        internal static void Reset()
        {
            _interactions.Clear();
            _live.Clear();
            Started = 0;
            Rescued = 0;
        }

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The same unbounded wait inside a dialogue: an NPC's gesture the conversation waits on (Kilian's salute
            // after "For freedom!"). On a copy the host drives, the action's end is never reported, so the dialogue
            // hung on the first greeting in a session and never solo.
            Hooking.TryPostfix(harmony, typeof(Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode), "OnExecute",
                typeof(SceneMoveWatch), nameof(InteractPostfix));

            Hooked = Hooking.TryPostfix(harmony, typeof(CS_Action_Move),
                nameof(CS_Action_Move.StartAction), typeof(SceneMoveWatch), nameof(StartActionPostfix));

            if (!Hooked)
            {
                MelonLogger.Warning("[Scene] cutscene moves cannot be bounded here; a scene whose actor " +
                    "never arrives will stop the dialogue it is part of for good.");
            }
        }

        /// <summary>
        /// Advances the watched moves. Call once per frame from a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            UpdateInteractions(unscaledDeltaTime);

            if (_live.Count == 0) return;

            if (!CoopSession.IsLive)
            {
                _live.Clear();
                return;
            }

            foreach (KeyValuePair<IntPtr, (CS_Action_Move Action, float Waited, float Limit)> pair in _live)
            {
                CS_Action_Move action = pair.Value.Action;

                try
                {
                    if (action == null || !action._isMoving || action._activeActor == null)
                    {
                        _live.Remove(pair.Key);
                        continue;
                    }

                    float waited = pair.Value.Waited + unscaledDeltaTime;

                    if (waited < pair.Value.Limit)
                    {
                        _live[pair.Key] = (action, waited, pair.Value.Limit);
                        continue;
                    }

                    _live.Remove(pair.Key);

                    if (action._timer == null) continue;

                    // The author's own escape hatch, taken rather than reinvented: finishing the timer
                    // makes the coroutine's tail run - the one that resets the follow-path movement -
                    // where poking the moving flag directly would skip it and leave the actor walking.
                    action._timer.SetFinished();
                    Rescued++;

                    // And this player put where the scene wanted them: walked from wherever they stood, not the talk spot
                    // the author assumed, they ran into a wall for the whole bound (Red Tower floor 3). Only the player:
                    // an NPC's mark can be stale - a teammate's scene already ported it on - and teleporting NPCs to marks
                    // made people pop around (see CutscenePorts.SettleDrivenWalk).
                    UnityEngine.Transform? target = action._targetInfo?.TargetTransform;
                    if (target != null && Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor player)
                        && player.Pointer == action._activeActor.Pointer)
                    {
                        action._activeActor.TeleportTo((UnityEngine.Vector2)target.position, portWithGroup: false, updateCam: true);
                    }

                    string who = action._activeActor?.name ?? "an actor";

                    MelonLogger.Warning($"[Scene] a cutscene move on '{who}' did not arrive within " +
                        $"{pair.Value.Limit:0}s; its timer was finished so the dialogue could continue. " +
                        $"Rescued so far: {Rescued}.");
                }
                catch (Exception e)
                {
                    _live.Remove(pair.Key);
                    MelonLogger.Error("[Scene] bounding a cutscene move failed: " + e);
                }
            }
        }

        private static void StartActionPostfix(CS_Action_Move __instance)
        {
            try
            {
                Started++;

                if (!CoopSession.IsLive) return;
                if (__instance == null || !__instance._isMoving) return;
                CutscenePorts.ReportWalk(__instance);
                if (CutscenePorts.SettleDrivenWalk(__instance)) return;
                if (_live.Count >= MaxWatched) return;

                float limit = Math.Max(__instance._maxTime, MinimumBoundSeconds) * 2f;

                _live[__instance.Pointer] = (__instance, 0f, limit);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Scene] watching a cutscene move failed: " + e);
            }
        }
    
        /// <summary>How long a dialogue waits on an NPC's gesture before it is let go. Gestures last a few seconds.</summary>
        private const float InteractionBoundSeconds = 20f;

        private static readonly Dictionary<IntPtr, (Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode Node, float Waited)> _interactions = new();

        private static void InteractPostfix(Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode __instance)
        {
            if (CoopSession.IsLive && __instance != null && _interactions.Count < MaxWatched) _interactions[__instance.Pointer] = (__instance, 0f);
        }

        private static void UpdateInteractions(float unscaledDeltaTime)
        {
            if (_interactions.Count == 0) return;

            foreach (KeyValuePair<IntPtr, (Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode Node, float Waited)> pair in _interactions.ToArray())
            {
                try
                {
                    Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode node = pair.Value.Node;
                    if (!CoopSession.IsLive || node == null || node._isInteractionFinished || !node._waitForFinish)
                    {
                        _interactions.Remove(pair.Key);
                        continue;
                    }

                    float waited = pair.Value.Waited + unscaledDeltaTime;
                    if (waited < InteractionBoundSeconds)
                    {
                        _interactions[pair.Key] = (node, waited);
                        continue;
                    }

                    _interactions.Remove(pair.Key);
                    node._isInteractionFinished = true;
                    Rescued++;
                    MelonLogger.Warning($"[Scene] a dialogue waited {InteractionBoundSeconds:0}s on an NPC's gesture that never " +
                        $"reported its end; it was let go so the conversation could continue. Rescued so far: {Rescued}.");
                }
                catch (Exception e)
                {
                    _interactions.Remove(pair.Key);
                    MelonLogger.Error("[Scene] bounding a dialogue gesture failed: " + e);
                }
            }
        }
    }
}
