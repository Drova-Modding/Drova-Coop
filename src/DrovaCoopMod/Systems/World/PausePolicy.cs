using Drova_Modding_API;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World.Patches;
using Il2CppDrova;
using Il2CppDrova.GUI;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Keeps one player's pause menu from freezing the world while a session is live. The pause requests
    /// are inlined, so the freeze is undone rather than refused.
    /// </summary>
    internal sealed class PausePolicy : ICoopSystem
    {
        private static bool _reportedThisFreeze;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // The private funnel every pause request reaches.
            Hooking.TryPostfix(harmony, typeof(GameState_TimeScale), "CheckIsGamePaused",
                typeof(PausePolicyPatches), nameof(PausePolicyPatches.CheckIsGamePausedPostfix));

            Hooking.TryPrefix(harmony, typeof(GUI_SliderGame), "Update",
                typeof(PausePolicyPatches), nameof(PausePolicyPatches.SliderGameUpdatePrefix));
        }

        /// <summary>Dead-man's switch for a frozen world the postfix missed, such as a pause held when the session began.</summary>
        internal static void Tick()
        {
            if (!CoopSession.IsLive)
            {
                _reportedThisFreeze = false;
                return;
            }

            // An alt-tabbed host would otherwise freeze the world for everyone.
            // Set every frame because the game writes it back from its settings.
            Application.runInBackground = true;

            // Exactly zero, since slow-motion effects use small non-zero scales and must be left alone.
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

        /// <summary>
        /// Holds a minigame (lockpick, fishing, timing) while its explanation is up.
        /// The explanation relies on frozen time, so the fail bar would run out while the player reads.
        /// </summary>
        internal static bool SliderGameUpdatePrefix(GUI_SliderGame __instance)
        {
            if (!CoopSession.IsLive) return true;

            GUI_Minigame_Explanation_Handler? explanation = __instance._minigameExplanationHandler;
            return explanation == null || !explanation.IsPaused;
        }

        /// <summary>
        /// Clears the freeze but keeps the pause list, so requests and releases still pair up.
        /// <c>EntityRigidbody.HandleMovement</c> skips every body while <c>_isGamePaused</c> is set.
        /// </summary>
        internal static void CheckIsGamePausedPostfix(GameState_TimeScale __instance)
        {
            if (!CoopSession.IsLive) return;

            __instance._isGamePaused = false;
            if (Time.timeScale == 0f) Time.timeScale = 1f;
        }
    }
}
