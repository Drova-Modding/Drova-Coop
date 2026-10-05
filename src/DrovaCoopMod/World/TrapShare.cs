using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.HitSystem;
using Il2CppDrova.InteractionSystem;
using Il2CppDrova.Traps;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// A creature's traps, one set for the party - Ebru's in the arena. The machine running her fight lays the real ones;
    /// every other machine shows the same traps at the same spots and takes each away with its original.
    ///
    /// The copy elsewhere replays her moves, and replaying the trap throw laid a second set there of its own, which fired
    /// on that screen alone: the two worlds held different traps. Those are taken back at once. The ones shown instead are
    /// inert - their hit receiver and disarm trigger switched off - because a teammate stepping on one already steps on the
    /// real one in the fighter's world, through their body there, and the blow reaches them from there.
    /// </summary>
    internal static class TrapShare
    {
        // How near a "gone" has to name a shown trap to take it away: the same spot, sent as floats.
        private const float SameSpot = 8f;

        private static readonly List<(GameObject Root, Vector2 At)> _laid = new();
        private static readonly List<(GameObject Root, Vector2 At)> _shown = new();
        private static readonly Dictionary<string, GameObject> _prefabs = new(StringComparer.Ordinal);

        /// <summary>Traps announced from here. For tests.</summary>
        internal static int Laid { get; private set; }

        /// <summary>Teammates' traps shown here. For tests.</summary>
        internal static int Shown { get; private set; }

        /// <summary>Traps a mirrored copy laid here and that were taken back. For tests.</summary>
        internal static int Withdrawn { get; private set; }

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(TrapController), nameof(TrapController.InitSetup), typeof(TrapShare), nameof(InitSetupPrefix));
        }

        private static bool InitSetupPrefix(TrapController __instance, bool isFromPlayer, Actor actor)
        {
            try
            {
                if (!CoopSession.IsLive || __instance == null || isFromPlayer || actor == null) return true;
                if (RemotePlayers.IsPlayersBody(actor) || !EnemyRegistry.TryGetSessionId(actor, out EnemyNetId id)) return true;

                GameObject? root = __instance._destroyRoot?.gameObject;
                if (root == null) return true;

                if (FogletMinions.Mirrors(actor))
                {
                    Withdrawn++;
                    UnityEngine.Object.Destroy(root);
                    return false;
                }

                // Laid by the creature this machine fights for real: the host's own, or a client's leased brawl.
                if (CoopSession.State == CoopSessionState.Joined && !EngageWatch.IsBrawling(id)) return true;

                Vector2 at = root.transform.position;
                _laid.Add((root, at));
                Laid++;
                CoopSession.ReportTrap(new TrapMessage { Op = TrapMessage.OpPlaced, Prefab = PrefabName(root), X = at.x, Y = at.y });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Trap] sharing a creature's trap failed: " + e);
            }

            return true;
        }

        /// <summary>Tells the others which of this machine's traps are gone. Once per frame.</summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            for (int index = _laid.Count - 1; index >= 0; index--)
            {
                if (_laid[index].Root != null) continue;

                Vector2 at = _laid[index].At;
                _laid.RemoveAt(index);
                CoopSession.ReportTrap(new TrapMessage { Op = TrapMessage.OpGone, Prefab = string.Empty, X = at.x, Y = at.y });
            }
        }

        /// <summary>A teammate's trap laid or gone. Called where the message arrives.</summary>
        internal static void Take(in TrapMessage message)
        {
            try
            {
                Vector2 at = new(message.X, message.Y);
                if (message.Op == TrapMessage.OpGone)
                {
                    int nearest = -1;
                    float best = SameSpot * SameSpot;
                    for (int index = 0; index < _shown.Count; index++)
                    {
                        float distance = (_shown[index].At - at).sqrMagnitude;
                        if (distance <= best) { best = distance; nearest = index; }
                    }

                    if (nearest < 0) return;
                    if (_shown[nearest].Root != null) UnityEngine.Object.Destroy(_shown[nearest].Root);
                    _shown.RemoveAt(nearest);
                    return;
                }

                if (!TryPrefab(message.Prefab, out GameObject prefab)) return;

                GameObject trap = UnityEngine.Object.Instantiate(prefab, new Vector3(at.x, at.y, 0f), Quaternion.identity);
                foreach (HitReceiveHandler receiver in trap.GetComponentsInChildren<HitReceiveHandler>(true)) receiver.gameObject.SetActive(false);
                foreach (InteractableTrigger disarm in trap.GetComponentsInChildren<InteractableTrigger>(true)) disarm.gameObject.SetActive(false);

                _shown.RemoveAll(shown => shown.Root == null);
                _shown.Add((trap, at));
                Shown++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Trap] showing a teammate's trap failed: " + e);
            }
        }

        /// <summary>Forgets the traps. Called when a session ends.</summary>
        internal static void Clear()
        {
            foreach ((GameObject root, _) in _shown) if (root != null) UnityEngine.Object.Destroy(root);
            _shown.Clear();
            _laid.Clear();
        }

        private static string PrefabName(GameObject root)
        {
            string name = root.name;
            return name.EndsWith("(Clone)", StringComparison.Ordinal) ? name[..^"(Clone)".Length].TrimEnd() : name;
        }

        // The prefab asset itself, an object of no scene. Loaded here because this machine's copy of the creature carries
        // the same ability.
        private static bool TryPrefab(string name, out GameObject prefab)
        {
            if (_prefabs.TryGetValue(name, out prefab!) && prefab != null) return true;

            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate == null || candidate.name != name || candidate.scene.IsValid()) continue;

                _prefabs[name] = prefab = candidate;
                return true;
            }

            return false;
        }
    }
}
