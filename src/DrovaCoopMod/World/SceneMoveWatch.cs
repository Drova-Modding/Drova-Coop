using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova.Cutscenes;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Bounds cutscene moves, whose <c>CS_Action_Move</c> max-time timer is never ticked, so an actor that
    /// never arrives would hang the dialogue forever. The bound is generous, twice the authored one.
    /// </summary>
    internal static class SceneMoveWatch
    {
        /// <summary>The floor under the authored bound, before it is doubled.</summary>
        private const float MinimumBoundSeconds = 15f;

        /// <summary>How many moves may be watched at once. A cutscene runs one or two.</summary>
        private const int MaxWatched = 64;

        private static readonly Dictionary<IntPtr, (CS_Action_Move Action, float Waited, float Limit)> Live = new();

        /// <summary>Whether the watch hook is in place.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>How many cutscene moves began here. Zero during a cutscene means an inlined hook.</summary>
        internal static int Started { get; private set; }

        /// <summary>How many had to be finished by hand. Should be zero in a healthy session.</summary>
        internal static int Rescued { get; private set; }

        /// <summary>How many moves are being watched right now.</summary>
        internal static int Watching => Live.Count;

        /// <summary>Whether a cutscene here is walking this body right now.</summary>
        internal static bool IsMoving(Il2CppDrova.Actor body)
        {
            foreach ((CS_Action_Move action, _, _) in Live.Values)
            {
                if (action != null && action._isMoving && action._activeActor != null
                    && action._activeActor.Pointer == body.Pointer) return true;
            }

            return false;
        }

        /// <summary>Ends a cutscene walk running here on this body, if there is one.</summary>
        internal static void Finish(Il2CppDrova.Actor body)
        {
            foreach (KeyValuePair<IntPtr, (CS_Action_Move Action, float Waited, float Limit)> pair in Live)
            {
                CS_Action_Move action = pair.Value.Action;
                if (action == null || !action._isMoving || action._activeActor == null || action._activeActor.Pointer != body.Pointer) continue;

                action._timer?.SetFinished();
                Live.Remove(pair.Key);
                return;
            }
        }

        /// <summary>Forgets what is being watched. Called when a session ends.</summary>
        internal static void Reset()
        {
            Interactions.Clear();
            Live.Clear();
            Started = 0;
            Rescued = 0;
        }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // Same unbounded wait for a dialogue gesture: on a host-driven copy the action's end is never reported.
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

        /// <summary>Advances the watched moves. Call once per frame from a loaded world.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            UpdateInteractions(unscaledDeltaTime);

            if (Live.Count == 0) return;

            if (!CoopSession.IsLive)
            {
                Live.Clear();
                return;
            }

            foreach (KeyValuePair<IntPtr, (CS_Action_Move Action, float Waited, float Limit)> pair in Live)
            {
                CS_Action_Move action = pair.Value.Action;

                try
                {
                    if (action == null || !action._isMoving || action._activeActor == null)
                    {
                        Live.Remove(pair.Key);
                        continue;
                    }

                    float waited = pair.Value.Waited + unscaledDeltaTime;

                    if (waited < pair.Value.Limit)
                    {
                        Live[pair.Key] = (action, waited, pair.Value.Limit);
                        continue;
                    }

                    Live.Remove(pair.Key);

                    if (action._timer == null) continue;

                    // Finishing the timer runs the coroutine's tail, which resets the follow-path movement.
                    action._timer.SetFinished();
                    Rescued++;

                    // Port the local player to the mark. Not NPCs, whose mark can be stale (see CutscenePorts.SettleDrivenWalk).
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
                    Live.Remove(pair.Key);
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
                if (Live.Count >= MaxWatched) return;

                float limit = Math.Max(__instance._maxTime, MinimumBoundSeconds) * 2f;

                Live[__instance.Pointer] = (__instance, 0f, limit);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Scene] watching a cutscene move failed: " + e);
            }
        }
    
        /// <summary>How long a dialogue waits on an NPC's gesture before it is let go. Gestures last a few seconds.</summary>
        private const float InteractionBoundSeconds = 20f;

        private static readonly Dictionary<IntPtr, (Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode Node, float Waited)> Interactions = new();

        private static void InteractPostfix(Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode __instance)
        {
            if (CoopSession.IsLive && __instance != null && Interactions.Count < MaxWatched) Interactions[__instance.Pointer] = (__instance, 0f);
        }

        private static void UpdateInteractions(float unscaledDeltaTime)
        {
            if (Interactions.Count == 0) return;

            foreach (KeyValuePair<IntPtr, (Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode Node, float Waited)> pair in Interactions.ToArray())
            {
                try
                {
                    Il2CppNodeCanvas.DialogueTrees.DS_InteractAABaseNode node = pair.Value.Node;
                    if (!CoopSession.IsLive || node == null || node._isInteractionFinished || !node._waitForFinish)
                    {
                        Interactions.Remove(pair.Key);
                        continue;
                    }

                    float waited = pair.Value.Waited + unscaledDeltaTime;
                    if (waited < InteractionBoundSeconds)
                    {
                        Interactions[pair.Key] = (node, waited);
                        continue;
                    }

                    Interactions.Remove(pair.Key);
                    node._isInteractionFinished = true;
                    Rescued++;
                    MelonLogger.Warning($"[Scene] a dialogue waited {InteractionBoundSeconds:0}s on an NPC's gesture that never " +
                        $"reported its end; it was let go so the conversation could continue. Rescued so far: {Rescued}.");
                }
                catch (Exception e)
                {
                    Interactions.Remove(pair.Key);
                    MelonLogger.Error("[Scene] bounding a dialogue gesture failed: " + e);
                }
            }
        }
    }
}
