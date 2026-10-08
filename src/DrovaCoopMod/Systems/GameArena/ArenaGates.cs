using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.Weather.Fog;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.GameArena
{
    /// <summary>
    /// Mirrors the game arena's gate barriers, Ada's fog and the darkness filter from the machine running the fight.
    /// The arena flags that switch them are per machine, so teammates would otherwise fight behind an open gate.
    /// </summary>
    internal sealed class ArenaGates : ICoopSystem
    {
        // A scan walks every object, so it only runs this near the ring.
        private const float NearRing = 1500f;

        private const float PollSeconds = 0.25f;
        private const float RescanSeconds = 5f;

        // Keeps reporting after the fight ends, while the gates open and the fog falls.
        private const float AfterFightSeconds = 15f;

        // Excludes the gladiators' sleeping area.
        private const float ArenaReach = 700f;

        // Matched by name. The parent path tells apart the ones that share a name.
        private static readonly string[] Gates = ["ActiveFight_Blocker", "Player_ArenaBlocker", "DaytimeLUT_Arena_DieDunkle"];
        private static readonly string[] Fogs = ["AdaFog_Fadeable_PreFight", "AdaFog_NonFadeable"];

        // The Remnants camp's ring.
        private static readonly Vector2 Ring = new(-1360f, 670f);

        private static readonly List<(GameObject Item, string Path)> Items = new();
        private static readonly Dictionary<string, byte> Known = new(StringComparer.Ordinal);

        // The player's own fights only. The arena runs NPC training fights daily in every world.
        private static readonly string[] Training = ["TrainingFight_FaceHauer", "TrainingFight_FaceCarima", "TrainingFight_FaceBlaan", "TrainingFight_FaceDark"];
        private static readonly AGVar<bool>?[] PlayerTraining = new AGVar<bool>?[4];

        private static float _sincePoll;
        private static float _sinceScan = RescanSeconds;
        private static float _sinceRunning = AfterFightSeconds;
        private static AGVar<bool>? _mainEvent;
        private static AGVar<bool>? _playerFightsMain;
        private static AGVar<bool>? _trainingStarted;

        /// <summary>Scene changes applied from a teammate's world. For tests.</summary>
        internal static int Applied { get; private set; }

        /// <summary>Watches the arena's scene.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
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
                if (_sinceScan >= RescanSeconds && (Items.Count == 0 || Items.Exists(item => item.Item == null)) && NearTheRing())
                {
                    _sinceScan = 0f;
                    Scan();
                }

                if (Items.Count == 0) return;
                if (RunsTheFight()) _sinceRunning = 0f;
                bool speaks = _sinceRunning < AfterFightSeconds;

                foreach ((GameObject item, string path) in Items)
                {
                    if (item == null) continue;

                    byte state = StateOf(item);
                    if (Known.TryGetValue(path, out byte was) && was == state) continue;

                    Known[path] = state;
                    if (speaks) ReportArenaGate(new ArenaGateMessage { Path = path, State = state });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] watching the arena's scene failed: " + e);
            }
        }

        /// <summary>A teammate's arena changed its scene.</summary>
        private static void Take(in ArenaGateMessage message)
        {
            try
            {
                if (Items.Count == 0 && NearTheRing()) Scan();

                foreach ((GameObject item, string path) in Items)
                {
                    if (item == null || path != message.Path) continue;

                    // Known first, so the poll does not tell the change back.
                    Known[path] = message.State;
                    Apply(item, message.State);
                    Applied++;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] matching a teammate's arena scene failed: " + e);
            }
        }

        /// <summary>Forgets the scene.</summary>
        private static void Clear()
        {
            Items.Clear();
            Known.Clear();
            _sinceScan = RescanSeconds;
            _sinceRunning = AfterFightSeconds;
            _mainEvent = null;
            _playerFightsMain = null;
            _trainingStarted = null;
            Array.Clear(PlayerTraining);
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
                // Risen at once, as FogletShare wakes a patch. Ada's fog has no foglets.
                if (fog._fogAnimation != null) fog._fogAnimation.SetPercent(1f);
                fog._curFadeTime = fog._fadeInTime;
                fog._hasSpawnedFoglets = true;
            }

            fog.SetMode(mode);
        }

        /// <summary>Whether a position is at the arena: the ring, the stands and the master's spots.</summary>
        internal static bool AtTheArena(Vector2 position) => (position - Ring).sqrMagnitude < ArenaReach * ArenaReach;

        /// <summary>Whether an arena fight is on here (its barrier stands, run here or mirrored) and the local player is at it.</summary>
        internal static bool LocalInAFight()
        {
            if (!Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor player) || !AtTheArena(player.GetFeetPosition())) return false;

            foreach ((GameObject item, _) in Items)
            {
                if (item != null && item.name == Gates[0] && item.activeInHierarchy) return true;
            }

            return false;
        }

        /// <summary>Whether this machine's own arena is running the player's fight, making its scene the true one.</summary>
        internal static bool RunsTheFight()
        {
            _mainEvent ??= Flag("MainFight_EventActive");
            _playerFightsMain ??= Flag("MainFight_PlayerIsFighting");
            if ((_mainEvent?.GetValue() ?? false) && (_playerFightsMain?.GetValue() ?? false)) return true;

            _trainingStarted ??= Flag("TrainingFight_StartFight");
            if (!(_trainingStarted?.GetValue() ?? false)) return false;

            for (int index = 0; index < Training.Length; index++)
            {
                PlayerTraining[index] ??= Flag(Training[index]);
                if (PlayerTraining[index]?.GetValue() ?? false) return true;
            }

            return false;
        }

        private static AGVar<bool>? Flag(string name)
        {
            SubDatabase_GVars? database = Drova_Modding_API.Access.ProviderAccess.GVarDatabase;
            if (database == null) return null;

            for (int index = 0; index < database.AllGVars.Count; index++)
            {
                GVarList? list = database.AllGVars[index];
                if (list == null || list.name != "DAS_ArenaSystem") continue;

                AGVarBase? found = list.GetGVarByName(name);
                return found == null ? null : found.TryCast<AGVar<bool>>();
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
            Items.Clear();
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate == null || !candidate.scene.IsValid()) continue;
                if (Array.IndexOf(Gates, candidate.name) < 0 && Array.IndexOf(Fogs, candidate.name) < 0) continue;

                Transform? parent = candidate.transform.parent;
                Items.Add((candidate, (parent != null ? parent.name : string.Empty) + "/" + candidate.name));
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<ArenaGateMessage>(MessageIds.ArenaGate, (peer, m) =>
            {
                if (CoopSession.State == CoopSessionState.Hosting) CoopSession.SendToClientsExcept(m, peer.Id);
                else if (!CoopSession.IsFromHost()) return;
                Take(m);
            });
        }

        /// <summary>A game-arena gate barrier switched here: to everyone (a client's goes through the host).</summary>
        private static void ReportArenaGate(in ArenaGateMessage message)
            => CoopSession.SendIfLive(message);

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Clear();
    }
}
