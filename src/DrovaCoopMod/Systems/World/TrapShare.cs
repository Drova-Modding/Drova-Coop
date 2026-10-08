using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using Il2CppDrova.HitSystem;
using Il2CppDrova.InteractionSystem;
using Il2CppDrova.Traps;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// One set of creature traps for the party (Ebru's in the arena). The fighting machine lays the real ones.
    /// Others drop their mirrored copy's traps and show inert copies, since the real trap hits them through their body there.
    /// </summary>
    internal sealed class TrapShare : ICoopSystem
    {
        // How near a "gone" has to name a shown trap to take it away.
        private const float SameSpot = 8f;

        private static readonly List<(GameObject Root, Vector2 At)> LaidTraps = new();
        private static readonly List<(GameObject Root, Vector2 At)> ShownTraps = new();
        private static readonly Dictionary<string, GameObject> Prefabs = new(StringComparer.Ordinal);

        /// <summary>Traps announced from here. For tests.</summary>
        internal static int Laid { get; private set; }

        /// <summary>Teammates' traps shown here. For tests.</summary>
        internal static int Shown { get; private set; }

        /// <summary>Traps a mirrored copy laid here and that were taken back. For tests.</summary>
        internal static int Withdrawn { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(TrapController), nameof(TrapController.InitSetup), typeof(TrapSharePatches), nameof(TrapSharePatches.InitSetupPrefix));
        }

        internal static bool InitSetupPrefix(TrapController __instance, bool isFromPlayer, Actor actor)
        {
            try
            {
                if (!CoopSession.IsLive || __instance == null || isFromPlayer || actor == null) return true;
                if (RemotePlayers.IsPlayersBody(actor) || !EnemyRegistry.TryGetSessionId(actor, out EnemyNetId id)) return true;

                if (__instance._destroyRoot == null) return true;
                GameObject? root = __instance._destroyRoot.gameObject;
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
                LaidTraps.Add((root, at));
                Laid++;
                ReportTrap(new TrapMessage { Op = TrapMessage.OpPlaced, Prefab = PrefabName(root), X = at.x, Y = at.y });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Trap] sharing a creature's trap failed: " + e);
            }

            return true;
        }

        /// <summary>Tells the others which of this machine's traps are gone.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            for (int index = LaidTraps.Count - 1; index >= 0; index--)
            {
                if (LaidTraps[index].Root != null) continue;

                Vector2 at = LaidTraps[index].At;
                LaidTraps.RemoveAt(index);
                ReportTrap(new TrapMessage { Op = TrapMessage.OpGone, Prefab = string.Empty, X = at.x, Y = at.y });
            }
        }

        /// <summary>A teammate's trap laid or gone. Called where the message arrives.</summary>
        private static void Take(in TrapMessage message)
        {
            try
            {
                Vector2 at = new(message.X, message.Y);
                if (message.Op == TrapMessage.OpGone)
                {
                    int nearest = -1;
                    float best = SameSpot * SameSpot;
                    for (int index = 0; index < ShownTraps.Count; index++)
                    {
                        float distance = (ShownTraps[index].At - at).sqrMagnitude;
                        if (distance <= best) { best = distance; nearest = index; }
                    }

                    if (nearest < 0) return;
                    if (ShownTraps[nearest].Root != null) UnityEngine.Object.Destroy(ShownTraps[nearest].Root);
                    ShownTraps.RemoveAt(nearest);
                    return;
                }

                if (!TryPrefab(message.Prefab, out GameObject? prefab)) return;

                GameObject trap = UnityEngine.Object.Instantiate(prefab, new Vector3(at.x, at.y, 0f), Quaternion.identity);
                foreach (HitReceiveHandler receiver in trap.GetComponentsInChildren<HitReceiveHandler>(true)) receiver.gameObject.SetActive(false);
                foreach (InteractableTrigger disarm in trap.GetComponentsInChildren<InteractableTrigger>(true)) disarm.gameObject.SetActive(false);

                ShownTraps.RemoveAll(shown => shown.Root == null);
                ShownTraps.Add((trap, at));
                Shown++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Trap] showing a teammate's trap failed: " + e);
            }
        }

        /// <summary>Forgets the traps. Called when a session ends.</summary>
        private static void Clear()
        {
            foreach ((GameObject root, _) in ShownTraps) if (root != null) UnityEngine.Object.Destroy(root);
            ShownTraps.Clear();
            LaidTraps.Clear();
        }

        private static string PrefabName(GameObject root)
        {
            string name = root.name;
            return name.EndsWith("(Clone)", StringComparison.Ordinal) ? name[..^"(Clone)".Length].TrimEnd() : name;
        }

        // The prefab asset is loaded here because this machine's copy of the creature carries the same ability.
        private static bool TryPrefab(string name, [NotNullWhen(true)] out GameObject? prefab)
        {
            if (Prefabs.TryGetValue(name, out prefab) && prefab != null) return true;

            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate == null || candidate.name != name || candidate.scene.IsValid()) continue;

                Prefabs[name] = prefab = candidate;
                return true;
            }

            return false;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<TrapMessage>(MessageIds.Trap, (peer, m) =>
            {
                if (CoopSession.State == CoopSessionState.Hosting) CoopSession.SendToClientsExcept(m, peer.Id);
                else if (!CoopSession.IsFromHost()) return;
                Take(m);
            });
        }

        /// <summary>A creature's trap laid or gone here: to everyone (a client's goes through the host).</summary>
        private static void ReportTrap(in TrapMessage message)
            => CoopSession.SendIfLive(message);

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Clear();
    }
}
