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
        private static readonly List<IntPtr> _finished = new();

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

        /// <summary>Forgets what is being watched. Called when a session ends.</summary>
        internal static void Reset()
        {
            _live.Clear();
            _finished.Clear();
            Started = 0;
            Rescued = 0;
        }

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
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
            if (_live.Count == 0) return;

            if (!CoopSession.IsLive)
            {
                _live.Clear();
                return;
            }

            _finished.Clear();

            foreach (KeyValuePair<IntPtr, (CS_Action_Move Action, float Waited, float Limit)> pair in _live)
            {
                CS_Action_Move action = pair.Value.Action;

                try
                {
                    if (action == null || !action._isMoving || action._activeActor == null)
                    {
                        _finished.Add(pair.Key);
                        continue;
                    }

                    float waited = pair.Value.Waited + unscaledDeltaTime;

                    if (waited < pair.Value.Limit)
                    {
                        _live[pair.Key] = (action, waited, pair.Value.Limit);
                        continue;
                    }

                    _finished.Add(pair.Key);

                    if (action._timer == null) continue;

                    // The author's own escape hatch, taken rather than reinvented: finishing the timer
                    // makes the coroutine's tail run - the one that resets the follow-path movement -
                    // where poking the moving flag directly would skip it and leave the actor walking.
                    action._timer.SetFinished();
                    Rescued++;

                    string who = action._activeActor?.name ?? "an actor";

                    MelonLogger.Warning($"[Scene] a cutscene move on '{who}' did not arrive within " +
                        $"{pair.Value.Limit:0}s; its timer was finished so the dialogue could continue. " +
                        $"Rescued so far: {Rescued}.");
                }
                catch (Exception e)
                {
                    _finished.Add(pair.Key);
                    MelonLogger.Error("[Scene] bounding a cutscene move failed: " + e);
                }
            }

            for (int index = 0; index < _finished.Count; index++)
            {
                _live.Remove(_finished[index]);
            }

            _finished.Clear();
        }

        private static void StartActionPostfix(CS_Action_Move __instance)
        {
            try
            {
                Started++;

                if (!CoopSession.IsLive) return;
                if (__instance == null || !__instance._isMoving) return;
                if (_live.Count >= MaxWatched) return;

                float limit = Math.Max(__instance._maxTime, MinimumBoundSeconds) * 2f;

                _live[__instance.Pointer] = (__instance, 0f, limit);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Scene] watching a cutscene move failed: " + e);
            }
        }
    }
}
