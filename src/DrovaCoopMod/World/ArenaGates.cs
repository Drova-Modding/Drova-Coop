using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.Weather.Fog;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// The game arena's scene, the same in every world while a fight is on: the gate barriers, Ada's fog and Sinistra's
    /// darkness (a colour filter switched on by a trigger that sees only the local player). All are
    /// switched by the arena's own flags, and those are kept per machine on purpose (each world books its own player) - so
    /// the gate shut and the fog rose only in the fighter's world: a teammate fighting beside them walked in through an
    /// open gate and fought Ada in clear air. The machine whose arena runs the fight tells the others what changed, and
    /// they set theirs to match. Only that machine: a teammate's own fog logic (walking out of a patch) must not talk back
    /// into the fight.
    /// </summary>
    internal static class ArenaGates
    {
        // The barriers and fog patches by their own names; the paths that carry them tell apart the ones that share a name.
        private static readonly string[] Gates = ["ActiveFight_Blocker", "Player_ArenaBlocker", "DaytimeLUT_Arena_DieDunkle"];
        private static readonly string[] Fogs = ["AdaFog_Fadeable_PreFight", "AdaFog_NonFadeable"];

        // The Remnants camp's ring, and how near a player has to be for its scene to be looked for at all: a scan is
        // every object in the world, not something to run anywhere else.
        private static readonly Vector2 Ring = new(-1360f, 670f);
        private const float NearRing = 1500f;

        private const float PollSeconds = 0.25f;
        private const float RescanSeconds = 5f;

        // How long after its fight ends a machine still speaks: the gates open and the fog falls as the event closes.
        private const float AfterFightSeconds = 15f;

        private static readonly List<(GameObject Item, string Path)> _items = new();
        private static readonly Dictionary<string, byte> _known = new(StringComparer.Ordinal);
        private static float _sincePoll;
        private static float _sinceScan = RescanSeconds;
        private static float _sinceRunning = AfterFightSeconds;
        // This machine's player's own fight: the main event with the player booked, or a training fight the player was
        // matched into. Not the fight flags alone - the arena runs NPC training fights daily in every world.
        private static AGVar<bool>? _mainEvent;
        private static AGVar<bool>? _playerFightsMain;
        private static AGVar<bool>? _trainingStarted;
        private static readonly AGVar<bool>?[] _playerTraining = new AGVar<bool>?[4];
        private static readonly string[] Training = ["TrainingFight_FaceHauer", "TrainingFight_FaceCarima", "TrainingFight_FaceBlaan", "TrainingFight_FaceDark"];

        /// <summary>Scene changes applied from a teammate's world. For tests.</summary>
        internal static int Applied { get; private set; }

        /// <summary>Watches the arena's scene. Once per frame from a loaded world.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            _sincePoll += unscaledDeltaTime;
            _sinceScan += unscaledDeltaTime;
            _sinceRunning += unscaledDeltaTime;
            if (_sincePoll < PollSeconds) return;
            _sincePoll = 0f;

            try
            {
                if (_sinceScan >= RescanSeconds && (_items.Count == 0 || _items.Exists(item => item.Item == null)) && NearTheRing())
                {
                    _sinceScan = 0f;
                    Scan();
                }

                if (_items.Count == 0) return;
                if (RunsTheFight()) _sinceRunning = 0f;
                bool speaks = _sinceRunning < AfterFightSeconds;

                foreach ((GameObject item, string path) in _items)
                {
                    if (item == null) continue;

                    byte state = StateOf(item);
                    if (_known.TryGetValue(path, out byte was) && was == state) continue;

                    _known[path] = state;
                    if (speaks) CoopSession.ReportArenaGate(new ArenaGateMessage { Path = path, State = state });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] watching the arena's scene failed: " + e);
            }
        }

        /// <summary>A teammate's arena changed its scene. Called where the message arrives.</summary>
        internal static void Take(in ArenaGateMessage message)
        {
            try
            {
                if (_items.Count == 0 && NearTheRing()) Scan();

                foreach ((GameObject item, string path) in _items)
                {
                    if (item == null || path != message.Path) continue;

                    // Known first, so the poll does not tell the change back.
                    _known[path] = message.State;
                    Apply(item, message.State);
                    Applied++;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] matching a teammate's arena scene failed: " + e);
            }
        }

        /// <summary>Forgets the scene. Called when a world or session ends.</summary>
        internal static void Clear()
        {
            _items.Clear();
            _known.Clear();
            _sinceScan = RescanSeconds;
            _sinceRunning = AfterFightSeconds;
            _mainEvent = null;
            _playerFightsMain = null;
            _trainingStarted = null;
            Array.Clear(_playerTraining);
        }

        private static byte StateOf(GameObject item)
        {
            FogletFogBhvr? fog = item.GetComponent<FogletFogBhvr>();
            return fog != null ? (byte)fog._mode : (byte)(item.activeSelf ? 1 : 0);
        }

        private static void Apply(GameObject item, byte state)
        {
            FogletFogBhvr? fog = item.GetComponent<FogletFogBhvr>();
            if (fog == null)
            {
                item.SetActive(state != 0);
                return;
            }

            FogletFogBhvr.Mode mode = (FogletFogBhvr.Mode)state;
            if (mode == FogletFogBhvr.Mode.Active)
            {
                // Risen at once, as FogletShare wakes a patch: no fade still to run, no foglets to roll (Ada's fog has none).
                fog._fogAnimation?.SetPercent(1f);
                fog._curFadeTime = fog._fadeInTime;
                fog._hasSpawnedFoglets = true;
            }

            fog.SetMode(mode);
        }

        /// <summary>
        /// Whether an actor stands at the arena: the ring, the stands and the master's spots - what a running arena event
        /// stages. For EngageWatch and NpcPlacement, which leave the staging to the machine running the event.
        /// </summary>
        internal static bool AtTheArena(Vector2 position) => (position - Ring).sqrMagnitude < ArenaReach * ArenaReach;

        /// <summary>Whether an arena fight is on here (its barrier stands, run here or mirrored) and the local player is at it.</summary>
        internal static bool LocalInAFight()
        {
            if (!Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor player) || !AtTheArena(player.GetFeetPosition())) return false;

            foreach ((GameObject item, _) in _items)
            {
                if (item != null && item.name == Gates[0] && item.activeInHierarchy) return true;
            }

            return false;
        }

        // From the ring's middle out over the stands and the master's spots; the gladiators' sleeping area is outside.
        private const float ArenaReach = 700f;

        /// <summary>Whether this machine's own arena is running a fight now - the one whose scene is the true one.</summary>
        internal static bool RunsTheFight()
        {
            _mainEvent ??= Flag("MainFight_EventActive");
            _playerFightsMain ??= Flag("MainFight_PlayerIsFighting");
            if ((_mainEvent?.GetValue() ?? false) && (_playerFightsMain?.GetValue() ?? false)) return true;

            _trainingStarted ??= Flag("TrainingFight_StartFight");
            if (!(_trainingStarted?.GetValue() ?? false)) return false;

            for (int index = 0; index < Training.Length; index++)
            {
                _playerTraining[index] ??= Flag(Training[index]);
                if (_playerTraining[index]?.GetValue() ?? false) return true;
            }

            return false;
        }

        private static AGVar<bool>? Flag(string name)
        {
            var database = Drova_Modding_API.Access.ProviderAccess.GVarDatabase;
            if (database == null) return null;

            for (int index = 0; index < database.AllGVars.Count; index++)
            {
                GVarList list = database.AllGVars[index];
                if (list != null && list.name == "DAS_ArenaSystem") return list.GetGVarByName(name)?.TryCast<AGVar<bool>>();
            }

            return null;
        }

        private static bool NearTheRing()
        {
            return Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor player)
                && ((Vector2)player.transform.position - Ring).sqrMagnitude < NearRing * NearRing;
        }

        private static void Scan()
        {
            _items.Clear();
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate == null || !candidate.scene.IsValid()) continue;
                if (Array.IndexOf(Gates, candidate.name) < 0 && Array.IndexOf(Fogs, candidate.name) < 0) continue;

                Transform? parent = candidate.transform.parent;
                _items.Add((candidate, (parent != null ? parent.name : string.Empty) + "/" + candidate.name));
            }
        }
    }
}
