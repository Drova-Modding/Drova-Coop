using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova.Utilities.LazyLoading;
using Il2CppDrova.Weather.Fog;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// When a foglet patch turns Active on any machine, every copy turns Active too. Leaving stays local.
    /// </summary>
    internal static class FogletShare
    {
        private static bool _applying;

        // Wakes for patches that were not loaded when they arrived, applied when the patch is met.
        private static readonly HashSet<string> Pending = new(StringComparer.Ordinal);

        /// <summary>Patches woken here because a teammate's woke. For tests.</summary>
        internal static int Woken { get; private set; }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // ActivateFoglets, not SetMode: SetMode is inlined, so a patch there never runs.
            Hooking.TryPostfix(harmony, typeof(FogletFogBhvr), "ActivateFoglets", typeof(FogletShare), nameof(ActivatedPostfix));
            Hooking.TryPrefix(harmony, typeof(FogletFogBhvr), "OnEnterHitReceiver", typeof(FogletShare), nameof(EnterPrefix));
            Hooking.TryPrefix(harmony, typeof(FogletFogBhvr), "OnExitHitReceiver", typeof(FogletShare), nameof(ExitPrefix));
        }

        private static void ActivatedPostfix(FogletFogBhvr __instance)
        {
            if (_applying || __instance == null || !CoopSession.IsLive) return;

            string? key = KeyOf(__instance);
            if (key != null) CoopSession.ReportFogletWake(new FogletWakeMessage { Key = key });
        }

        // A player walking into a patch that is already out would knock it back to Dorment and start the roll again.
        private static bool EnterPrefix(FogletFogBhvr __instance)
        {
            if (__instance == null) return true;

            if (Pending.Count > 0 && KeyOf(__instance) is { } key && Pending.Remove(key)) Take(new FogletWakeMessage { Key = key });

            if (__instance._mode != FogletFogBhvr.Mode.Active) return true;

            __instance._isPlayerInside = true;
            return false;
        }

        // The host's player leaving resets the real foglets, so the host's patch stays while a teammate is inside.
        private static bool ExitPrefix(FogletFogBhvr __instance)
        {
            if (CoopSession.State != CoopSessionState.Hosting || __instance?._hitReceiver?.FogCollider is not { } fog) return true;

            foreach (RemotePlayer body in RemotePlayers.Bodies.Values)
            {
                if (body?.Body == null || !fog.OverlapPoint(body.Body.GetFeetPosition())) continue;

                __instance._isPlayerInside = false;
                return false;
            }

            return true;
        }

        /// <summary>Forgets wakes still waiting for their patch. Called when a session ends.</summary>
        internal static void Reset() => Pending.Clear();

        /// <summary>A teammate's patch woke: wake this copy, unless it is beaten, gone, or already out.</summary>
        internal static void Take(in FogletWakeMessage message)
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
                        patch._fogAnimation?.SetPercent(1f);
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
            if (patch?._foglets == null) return null;

            foreach (LazyActor? foglet in patch._foglets)
            {
                if (foglet?.GuidComponent?.GuidString is { Length: > 0 } guid) return guid;
            }

            return null;
        }
    }
}
