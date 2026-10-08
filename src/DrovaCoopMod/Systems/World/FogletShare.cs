using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova.Utilities.LazyLoading;
using Il2CppDrova.Weather.Fog;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// When a foglet patch turns Active on any machine, every copy turns Active too. Leaving stays local.
    /// </summary>
    internal sealed class FogletShare : ICoopSystem
    {
        private static bool _applying;

        // Wakes for patches that were not loaded when they arrived, applied when the patch is met.
        private static readonly HashSet<string> Pending = new(StringComparer.Ordinal);

        /// <summary>Patches woken here because a teammate's woke. For tests.</summary>
        internal static int Woken { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // ActivateFoglets, not SetMode: SetMode is inlined, so a patch there never runs.
            Hooking.TryPostfix(harmony, typeof(FogletFogBhvr), "ActivateFoglets", typeof(FogletSharePatches), nameof(FogletSharePatches.ActivatedPostfix));
            Hooking.TryPrefix(harmony, typeof(FogletFogBhvr), "OnEnterHitReceiver", typeof(FogletSharePatches), nameof(FogletSharePatches.EnterPrefix));
            Hooking.TryPrefix(harmony, typeof(FogletFogBhvr), "OnExitHitReceiver", typeof(FogletSharePatches), nameof(FogletSharePatches.ExitPrefix));
        }

        internal static void ActivatedPostfix(FogletFogBhvr __instance)
        {
            if (_applying || __instance == null || !CoopSession.IsLive) return;

            string? key = KeyOf(__instance);
            if (key != null) ReportFogletWake(new FogletWakeMessage { Key = key });
        }

        // A player walking into a patch that is already out would knock it back to Dorment and start the roll again.
        internal static bool EnterPrefix(FogletFogBhvr __instance)
        {
            if (__instance == null) return true;

            if (Pending.Count > 0 && KeyOf(__instance) is { } key && Pending.Remove(key)) Take(new FogletWakeMessage { Key = key });

            if (__instance._mode != FogletFogBhvr.Mode.Active) return true;

            __instance._isPlayerInside = true;
            return false;
        }

        // The host's player leaving resets the real foglets, so the host's patch stays while a teammate is inside.
        internal static bool ExitPrefix(FogletFogBhvr __instance)
        {
            if (CoopSession.State != CoopSessionState.Hosting || __instance._hitReceiver == null) return true;

            UnityEngine.PolygonCollider2D fog =__instance._hitReceiver.FogCollider;
            if (fog == null) return true;

            foreach (RemotePlayer body in RemotePlayers.Bodies.Values)
            {
                if (body?.Body == null || !fog.OverlapPoint(body.Body.GetFeetPosition())) continue;

                __instance._isPlayerInside = false;
                return false;
            }

            return true;
        }

        /// <summary>Forgets wakes still waiting for their patch.</summary>
        private static void Reset() => Pending.Clear();

        /// <summary>A teammate's patch woke: wake this copy, unless it is beaten, gone, or already out.</summary>
        private static void Take(in FogletWakeMessage message)
        {
            try
            {
                foreach (FogletFogBhvr patch in UnityEngine.Object.FindObjectsOfType<FogletFogBhvr>())
                {
                    if (patch == null || KeyOf(patch) != message.Key) continue;
                    if (patch._defeated) return;
                    if (patch._mode is FogletFogBhvr.Mode.Active or FogletFogBhvr.Mode.FadeOut) return;

                    _applying = true;
                    try
                    {
                        if (patch._fogAnimation != null) patch._fogAnimation.SetPercent(1f);
                        patch._curFadeTime = patch._fadeInTime;
                        patch.SetMode(FogletFogBhvr.Mode.Active);

                        // Activated inside the guard, so it does not report a wake of its own.
                        patch._hasSpawnedFoglets = true;
                        patch.ActivateFoglets();
                    }
                    finally
                    {
                        _applying = false;
                    }

                    Woken++;
                    MelonLogger.Msg($"[Foglet] a teammate's fog patch woke; '{patch.name}' wakes here too.");
                    return;
                }

                // Not loaded here yet: woken when it is met.
                Pending.Add(message.Key);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Foglet] waking a fog patch failed: " + e);
            }
        }

        private static string? KeyOf(FogletFogBhvr patch)
        {
            if (patch._foglets == null) return null;

            foreach (LazyActor? foglet in patch._foglets)
            {
                if (foglet == null || foglet.GuidComponent == null) continue;

                string guid = foglet.GuidComponent.GuidString;
                if (!string.IsNullOrEmpty(guid)) return guid;
            }

            return null;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<FogletWakeMessage>(MessageIds.FogletWake, (peer, m) =>
            {
                if (CoopSession.State == CoopSessionState.Hosting) CoopSession.SendToClientsExcept(m, peer.Id);
                else if (!CoopSession.IsFromHost()) return;
                Take(m);
            });
        }

        /// <summary>A fog patch woke here: to everyone (a client's goes through the host).</summary>
        private static void ReportFogletWake(in FogletWakeMessage message)
            => CoopSession.SendIfLive(message);

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
