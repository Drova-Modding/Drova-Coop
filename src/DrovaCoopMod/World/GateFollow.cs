using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes sure a story gate that took a teammate takes this player too.
    ///
    /// A journey-shared gate runs its listeners here, but the port that moves the player is usually
    /// the next node of a sequence graph that only progressed on the machine that triggered it - so
    /// the listeners fire and the player stands still. This watch arms whenever such a gate arrives:
    /// if the game's own staging has not moved this player to their teammate within the grace, the
    /// mod moves them itself. A forced story beat must not depend on staging that never crosses.
    /// </summary>
    internal static class GateFollow
    {
        /// <summary>How long the game's own staging gets to do it first.</summary>
        private const float GraceSeconds = 8f;

        /// <summary>Standing farther than this from every teammate counts as left behind.</summary>
        private const float FollowDistance = 3000f;

        /// <summary>
        /// How long a player who just made a gate-sized jump of their own refuses to follow anybody.
        ///
        /// The player a gate took IS the teammate everyone else follows. Their own machine still arms,
        /// because the far world's quest reacting to the gate writes journey-shared variables that
        /// cross straight back - and the first captured client was yanked out of its own capture
        /// cutscene, back to the ambush, toward a teammate's stale position. A recent long port, story
        /// or <c>coop_tp</c>, means this player is where somebody meant them to be.
        /// </summary>
        private const float LeaderGraceSeconds = 25f;

        /// <summary>How long after a dialogue cutscene starts a long jump still counts as its port.</summary>
        private const float StoryPortWindowSeconds = 10f;

        private static bool _followedSelf;
        private static float _armedFor;
        private static float _ledFor;
        private static Vector2 _lastPosition;
        private static bool _hasLastPosition;

        /// <summary>How many gates this player was brought along for. For <c>coop_status</c>.</summary>
        internal static int Followed { get; private set; }

        /// <summary>Disarms. Called when a session ends.</summary>
        internal static void Reset()
        {
            _armedFor = 0f;
            _ledFor = 0f;
            _hasLastPosition = false;
            _followedSelf = false;
            Followed = 0;
            StoryPortsReported = 0;
        }

        /// <summary>
        /// Arms the watch. Called when a journey-shared gate arrives from the session.
        /// </summary>
        internal static void Arm()
        {
            // Not extended by a repeat: a forced dialogue waiting on this player re-arms every frame,
            // and an extending arm would never expire.
            if (_armedFor <= 0f) _armedFor = GraceSeconds;
        }

        /// <summary>
        /// Follows when the grace runs out. Call once per frame from a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                _armedFor = 0f;
                _ledFor = 0f;
                _hasLastPosition = false;
                return;
            }

            try
            {
                WatchOwnJumps(unscaledDeltaTime);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Gate] watching this player's own ports failed: " + e);
            }

            if (_armedFor <= 0f) return;

            _armedFor -= unscaledDeltaTime;
            if (_armedFor > 0f) return;

            try
            {
                Follow();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Gate] following a story gate failed: " + e);
            }
        }

        /// <summary>
        /// Notices the local player making a gate-sized jump, so a fresh port refuses to follow.
        /// </summary>
        private static void WatchOwnJumps(float unscaledDeltaTime)
        {
            if (_ledFor > 0f)
            {
                _ledFor -= unscaledDeltaTime;
            }

            if (!PlayerAccess.TryGetPlayer(out Actor self))
            {
                _hasLastPosition = false;
                return;
            }

            Vector2 position = self.GetFeetPosition();

            if (_hasLastPosition
                && (position - _lastPosition).sqrMagnitude > FollowDistance * FollowDistance)
            {
                _ledFor = LeaderGraceSeconds;

                if (_followedSelf) _followedSelf = false;
                else ReportIfStoryPorted();
            }

            _lastPosition = position;
            _hasLastPosition = true;
        }

        /// <summary>
        /// Tells teammates that this player's own story just carried them far away, so they come along.
        ///
        /// The capture, the Red Tower entry and every other story port is a teleport action inside a
        /// dialogue's cutscene, and a dialogue runs only on the machine of the player in it. So the far
        /// machine has to be told; nothing in its own world moves. A long jump counts when a dialogue
        /// cutscene started here moments before - which leaves out doors, interiors and coop_tp - and
        /// was not fast travel, which is one player's own trip.
        /// </summary>
        private static void ReportIfStoryPorted()
        {
            if (UnityEngine.Time.unscaledTime - CutsceneWatch.LastAt > StoryPortWindowSeconds) return;

            string name = CutsceneWatch.LastName;
            if (string.IsNullOrEmpty(name) || name.Contains("FastTravel", StringComparison.OrdinalIgnoreCase)) return;

            // A shared cutscene already told the session when it started, and is replayed there.
            if (Drova_Modding_API.Access.CutsceneSyncAccess.IsShared(name)) return;

            StoryPortsReported++;
            MelonLogger.Msg($"[Gate] '{name}' carried this player far; teammates are brought along.");

            CoopSession.ReportCutscene(new CutsceneMessage
            {
                Name = name,
                ActionId = CutsceneWatch.LastActionId,
            });
        }

        /// <summary>How many of this player's story ports were told to the session. For <c>coop_status</c>.</summary>
        internal static int StoryPortsReported { get; private set; }

        private static void Follow()
        {
            if (!PlayerAccess.TryGetPlayer(out Actor self)) return;

            // This player just rode a long port of their own - the story's, or a teammate's gate
            // already followed, or a coop_tp. They are where somebody meant them to be, and pulling
            // them toward a teammate's possibly stale position now would undo it: the first captured
            // client was torn out of its own capture cutscene this way.
            if (_ledFor > 0f) return;

            // Mid-dialogue or mid-cutscene the story still holds this player; try again shortly.
            if (self.DialogueModule != null && self.DialogueModule.IsInDialogue())
            {
                _armedFor = 1f;
                return;
            }

            Vector2 origin = self.GetFeetPosition();

            bool found = false;
            Vector2 nearest = default;
            float nearestSquared = float.MaxValue;

            foreach (KeyValuePair<PlayerId, RemotePlayer> pair in RemotePlayers.Bodies)
            {
                Actor body = pair.Value?.Body;
                if (body == null || body.IsDestroyed) continue;

                Vector2 position = body.GetFeetPosition();
                float squared = (position - origin).sqrMagnitude;

                if (squared >= nearestSquared) continue;

                found = true;
                nearest = position;
                nearestSquared = squared;
            }

            if (!found) return;
            if (nearestSquared <= FollowDistance * FollowDistance) return;

            _followedSelf = true;
            self.TeleportTo(nearest, portWithGroup: false, updateCam: true);
            Followed++;

            MelonLogger.Msg($"[Gate] a story gate took a teammate {Mathf.Sqrt(nearestSquared):0} units " +
                "away and this world's staging did not follow, so this player was brought along.");
        }
    }
}
