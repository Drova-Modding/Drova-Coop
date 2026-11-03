using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Moves this player to a teammate a journey-shared story gate took, since the port node runs only on the triggering machine.
    /// </summary>
    internal sealed class GateFollow : ICoopSystem
    {
        /// <summary>How long the game's own staging gets to move the player first.</summary>
        private const float GraceSeconds = 8f;

        /// <summary>Grace for a teammate's reported story port, long enough for their body to arrive here.</summary>
        private const float StoryPortSeconds = 1f;

        /// <summary>Farther than this from every teammate is left behind, and a jump this long is a port. Red Tower floors are about 3100 apart.</summary>
        private const float FollowDistance = 2000f;

        /// <summary>Gate variables cross back to the player the gate took, so a recent long port of their own blocks following.</summary>
        private const float LeaderGraceSeconds = 25f;

        /// <summary>How long after a cutscene teleported this player the jump may show up, since the port can wait for a fade.</summary>
        private const float StoryPortWindowSeconds = 3f;

        /// <summary>
        /// A teammate's report cannot be checked against data this world may not have loaded, so it moves this player
        /// at most this often. Story ports are minutes apart.
        /// </summary>
        private const float MinTeammatePortSeconds = 20f;

        private static float _lastTeammatePort = float.NegativeInfinity;
        private static bool _followedSelf;
        private static float _armedFor;
        private static float _ledFor;
        private static Vector2 _lastPosition;
        private static bool _hasLastPosition;

        /// <summary>Cutscenes that carry one player alone, like the tavern well's ladder.</summary>
        private static readonly HashSet<string> OwnTrips = new(StringComparer.Ordinal) { "CutsceneData_Well" };

        /// <summary>How many gates this player was brought along for. For <c>coop_status</c>.</summary>
        internal static int Followed { get; private set; }

        /// <summary>How many of this player's story ports were told to the session. For <c>coop_status</c>.</summary>
        internal static int StoryPortsReported { get; private set; }

        private static void Reset()
        {
            _armedFor = 0f;
            _ledFor = 0f;
            _hasLastPosition = false;
            _followedSelf = false;
            _lastTeammatePort = float.NegativeInfinity;
            Followed = 0;
            StoryPortsReported = 0;
        }

        /// <summary>Arms the watch when a journey-shared gate arrives.</summary>
        internal static void Arm(float grace = GraceSeconds)
        {
            // A forced dialogue re-arms every frame, so a repeat never extends the timer. A shorter one takes over.
            if (_armedFor <= 0f || grace < _armedFor) _armedFor = grace;
        }

        /// <summary>A teammate reported that a story cutscene carried them far. Ignored when one did so moments ago.</summary>
        internal static void ArmForTeammate()
        {
            if (Time.unscaledTime - _lastTeammatePort < MinTeammatePortSeconds)
            {
                MelonLogger.Warning("[Gate] another story port from a teammate came too soon after the last and was ignored.");
                return;
            }

            _lastTeammatePort = Time.unscaledTime;
            Arm(StoryPortSeconds);
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.SharesWorld)
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

        /// <summary>
        /// Tells teammates about a story port, a teleport inside a dialogue cutscene that runs only here.
        /// A long jump counts only right after a cutscene teleported this player, which leaves out doors, interiors and coop_tp.
        /// </summary>
        private static void ReportIfStoryPorted()
        {
            if (Time.unscaledTime - CutsceneWatch.PlayerPortedAt > StoryPortWindowSeconds) return;

            string name = CutsceneWatch.LastName;
            if (string.IsNullOrEmpty(name) || name.Contains("FastTravel", StringComparison.OrdinalIgnoreCase)) return;

            if (OwnTrips.Contains(name)) return;

            // A shared cutscene is already replayed on the other machines.
            if (CutsceneSyncAccess.IsShared(name)) return;

            StoryPortsReported++;
            MelonLogger.Msg($"[Gate] '{name}' carried this player far; teammates are brought along.");

            RemoteCutscene.ReportCutscene(new CutsceneMessage
            {
                Name = name,
                ActionId = CutsceneWatch.LastActionId,
            });
        }

        private static void Follow()
        {
            if (!PlayerAccess.TryGetPlayer(out Actor self)) return;

            // A recent long port of their own put this player where somebody meant them to be.
            if (_ledFor > 0f) return;

            // Mid-dialogue the story still holds this player, so it retries shortly.
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
                Actor? body = pair.Value?.Body;
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

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
