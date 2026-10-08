using Drova_Modding_API;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Keeps one player's pausing menu from freezing the world while a session is live. The pause requests
    /// in <c>GameState_TimeScale</c> are inlined, so the freeze is undone rather than refused.
    /// </summary>
    internal sealed class PausePolicy : ICoopSystem
    {
        private static bool _reportedThisFreeze;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // The private funnel every pause request reaches. The public entry points are inlined.
            Hooking.TryPostfix(harmony, typeof(GameState_TimeScale), "CheckIsGamePaused",
                typeof(PausePolicyPatches), nameof(PausePolicyPatches.CheckIsGamePausedPostfix));
        }

        /// <summary>Dead-man's switch for a frozen world the postfix missed, such as a pause held when the session began.</summary>
        internal static void Tick()
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
        internal static void CheckIsGamePausedPostfix(GameState_TimeScale __instance)
        {
            if (!CoopSession.IsLive) return;

            __instance._isGamePaused = false;
            if (Time.timeScale == 0f) Time.timeScale = 1f;
        }
    }
}
