using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps one player's pausing menu from freezing the world while a session is live. The pause requests
    /// in <c>GameState_TimeScale</c> are inlined, so the freeze is undone rather than refused.
    /// </summary>
    internal static class PausePolicy
    {
        private static bool _reportedThisFreeze;

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The private funnel every pause request reaches. The public entry points are inlined.
            Hooking.TryPostfix(harmony, typeof(GameState_TimeScale), "CheckIsGamePaused",
                typeof(PausePolicy), nameof(CheckIsGamePausedPostfix));
        }

        /// <summary>
        /// Dead-man's switch for a frozen world the postfix missed, such as a pause held when the session began.
        /// Call once per frame.
        /// </summary>
        internal static void Tick(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                _reportedThisFreeze = false;
                return;
            }

            // Exactly zero: slow-motion effects use small non-zero scales and must be left alone.
            if (Time.timeScale != 0f)
            {
                _reportedThisFreeze = false;
                return;
            }

            Time.timeScale = 1f;

            if (!_reportedThisFreeze)
            {
                _reportedThisFreeze = true;
                MelonLogger.Msg("[Coop] A window asked to freeze the world; time kept running because a session is live.");
            }
        }

        // The pause list stays intact so requests and releases still pair up. _isGamePaused must be cleared
        // too, since EntityRigidbody.HandleMovement returns early for every body while it is set.
        private static void CheckIsGamePausedPostfix(GameState_TimeScale __instance)
        {
            if (!CoopSession.IsLive) return;

            __instance._isGamePaused = false;
            if (Time.timeScale == 0f) Time.timeScale = 1f;
        }
    }
}
