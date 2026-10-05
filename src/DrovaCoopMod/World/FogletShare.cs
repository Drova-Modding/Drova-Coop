using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova.Weather.Fog;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// One foglet patch for the party. A patch wakes on the machine whose player walks into it, after a random number of
    /// ticks and a fade, so two players in the same fog met their foglets seconds apart - or one of them not at all while
    /// standing outside it. When a patch turns Active anywhere, every copy is put straight into Active too; the foglets
    /// themselves are shared creatures already, and so are their deaths and the patch's defeat that follows.
    ///
    /// Leaving stays local: a patch resets its foglets by whether its own player is inside, and mirroring the host's
    /// exit would hide foglets a teammate is still fighting.
    /// </summary>
    internal static class FogletShare
    {
        private static bool _applying;

        // Wakes for patches that were not loaded when they arrived, applied when the patch is met.
        private static readonly HashSet<string> _pending = new(StringComparer.Ordinal);

        /// <summary>Patches woken here because a teammate's woke. For tests.</summary>
        internal static int Woken { get; private set; }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // ActivateFoglets, not SetMode: SetMode is small enough to be inlined, and a patch there applied and never ran.
            // ActivateFoglets runs once each time a patch turns Active.
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

            if (_pending.Count > 0 && KeyOf(__instance) is { } key && _pending.Remove(key)) Take(new FogletWakeMessage { Key = key });

            if (__instance._mode != FogletFogBhvr.Mode.Active) return true;

            __instance._isPlayerInside = true;
            return false;
        }

        /// <summary>
        /// The host's player leaving resets the patch's foglets - stopped, hidden, sent home. On the host those are the
        /// real ones, and a teammate still fighting inside would see them vanish; so while one is in, the host's patch stays.
        /// </summary>
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
        internal static void Reset() => _pending.Clear();

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

                        // Out now, inside the guard: left to the next update, its activation reported a wake of its own.
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
                _pending.Add(message.Key);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Foglet] waking a fog patch failed: " + e);
            }
        }

        private static string? KeyOf(FogletFogBhvr patch)
        {
            if (patch?._foglets == null) return null;

            foreach (var foglet in patch._foglets)
            {
                if (foglet?.GuidComponent?.GuidString is { Length: > 0 } guid) return guid;
            }

            return null;
        }
    }
}
