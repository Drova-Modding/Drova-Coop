using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Moves this player to a teammate a journey-shared story gate took, since the port node runs only on the triggering machine.
    /// </summary>
    internal static class GateFollow
    {
        // How long the game's own staging gets to move the player first.
        private const float GraceSeconds = 8f;

        /// <summary>Grace for a teammate's reported story port: only long enough for their body to arrive here.</summary>
        internal const float StoryPortSeconds = 1f;

        // Farther than this from every teammate is left behind, and a jump this long is a port. Red Tower floors are ~3100 apart.
        private const float FollowDistance = 2000f;

        // The player a gate took is the leader. Gate variables cross back to them, so a recent long port of their own blocks following.
        private const float LeaderGraceSeconds = 25f;

        // How long after a dialogue cutscene starts a long jump still counts as its port.
        private const float StoryPortWindowSeconds = 10f;

        private static bool _followedSelf;
        private static float _armedFor;
        private static float _ledFor;
        private static Vector2 _lastPosition;
        private static bool _hasLastPosition;

        /// <summary>How many gates this player was brought along for. For <c>coop_status</c>.</summary>
        internal static int Followed { get; private set; }

        /// <summary>Disarms when a session ends.</summary>
        internal static void Reset()
        {
            _armedFor = 0f;
            _ledFor = 0f;
            _hasLastPosition = false;
            _followedSelf = false;
            Followed = 0;
            StoryPortsReported = 0;
        }

        /// <summary>Arms the watch when a journey-shared gate arrives.</summary>
        internal static void Arm(float grace = GraceSeconds)
        {
            // A forced dialogue re-arms every frame, so a repeat never extends the timer. A shorter one takes over.
            if (_armedFor <= 0f || grace < _armedFor) _armedFor = grace;
        }

        /// <summary>Follows when the grace runs out. Call once per frame from a loaded world.</summary>
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

        // Story ports are teleports inside a dialogue cutscene that runs only here, so teammates must be told.
        // A long jump counts only right after a dialogue cutscene, which leaves out doors, interiors and coop_tp.
        private static void ReportIfStoryPorted()
        {
            if (Time.unscaledTime - CutsceneWatch.LastAt > StoryPortWindowSeconds) return;

            string name = CutsceneWatch.LastName;
            if (string.IsNullOrEmpty(name) || name.Contains("FastTravel", StringComparison.OrdinalIgnoreCase)) return;

            // One player's own trips, like the tavern well's ladder.
            if (OwnTrips.Contains(name)) return;

            // A shared cutscene is already replayed on the other machines.
            if (CutsceneSyncAccess.IsShared(name)) return;

            StoryPortsReported++;
            MelonLogger.Msg($"[Gate] '{name}' carried this player far; teammates are brought along.");

            CoopSession.ReportCutscene(new CutsceneMessage
            {
                Name = name,
                ActionId = CutsceneWatch.LastActionId,
            });
        }

        private static readonly HashSet<string> OwnTrips = new(StringComparer.Ordinal) { "CutsceneData_Well" };

        /// <summary>How many of this player's story ports were told to the session. For <c>coop_status</c>.</summary>
        internal static int StoryPortsReported { get; private set; }

        private static void Follow()
        {
            if (!PlayerAccess.TryGetPlayer(out Actor self)) return;

            // A recent long port of their own put this player where somebody meant them to be.
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
