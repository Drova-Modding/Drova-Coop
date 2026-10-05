using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Stops one player's menu from freezing everybody else's game.
    ///
    /// Drova's real freeze is <c>GameState_TimeScale</c>, a reference-counted list of objects asking for a
    /// pause; when the list is non-empty it drives <c>Time.timeScale</c> to zero. Single-player that is
    /// correct. In a session it means opening the pause menu stops the world for the other player too,
    /// who is standing in it.
    ///
    /// So while a session is live, the clock is put back whenever something stops it. Note this is a
    /// smaller change than it sounds: most of Drova's windows never asked for a pause in the first place -
    /// inventory, journal, talents, map, merchant, loot, and all three dialogue windows leave
    /// <c>Time.timeScale</c> alone and merely block the acting player's input. Only a handful freeze.
    ///
    /// Undone rather than refused because refusing is not available: the two methods that register a pause
    /// are inlined into their caller, so a patch on either is accepted and then never runs.
    ///
    /// This is not only about the other player being unable to move. With the world stopped, nothing that
    /// depends on time advances either: no enemy acts, no attack resolves, no effect expires. A player who
    /// opens a pausing window mid-fight is therefore untouchable for as long as it stays open, while every
    /// enemy around them waits. Refusing the pause removes the freeze and the invulnerability together.
    /// </summary>
    internal static class PausePolicy
    {
        private static bool _reportedThisFreeze;

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // CheckIsGamePaused, not RequestGamePause. The public entry points are inlined into their
            // caller: patching them succeeds and then never runs, measured as zero invocations across
            // several pauses while this funnel - which only they call - recorded six. Both of them reach
            // it, so it is the one place that sees every pause however it was requested.
            Hooking.TryPostfix(harmony, typeof(GameState_TimeScale), "CheckIsGamePaused",
                typeof(PausePolicy), nameof(CheckIsGamePausedPostfix));
        }

        /// <summary>
        /// The dead-man's switch. Call once per frame.
        ///
        /// A world stuck at <c>Time.timeScale</c> zero cannot recover on its own: the code that would
        /// release the pause runs in a game that is no longer advancing, and every input that might reach
        /// it is behind a window that is no longer being drawn. The only way out is killing the process,
        /// which loses whatever was not saved.
        ///
        /// The prefixes below cannot cause that, but they also do not cover a pause already held when a
        /// session begins, or any request path not found by reading the callers. Rather than enumerate
        /// those, this treats the end state as the thing to detect: during a session a completely frozen
        /// world is always wrong, whatever put it there, so it is measured directly and undone.
        /// </summary>
        internal static void Tick(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                _reportedThisFreeze = false;
                return;
            }

            // Compared against zero exactly, not against a threshold. A pause drives the scale to zero;
            // slow-motion effects multiply it to something small but non-zero, and those are legitimate
            // and must not be overridden.
            if (Time.timeScale != 0f)
            {
                _reportedThisFreeze = false;
                return;
            }

            // Restored every frame it is found at zero, with no grace period. The refusal below is what
            // should prevent this, and it does not always fire; whatever the reason, a session where one
            // player can stop the world is worse than one where a pause menu fails to pause.
            Time.timeScale = 1f;

            if (!_reportedThisFreeze)
            {
                _reportedThisFreeze = true;
                MelonLogger.Msg("[Coop] A window asked to freeze the world; time kept running because a session is live.");
            }
        }

        /// <summary>
        /// Undoes the freeze the moment the game applies it.
        ///
        /// Leaves the pause list alone - it is the game's own bookkeeping, every request is paired with a
        /// release, and emptying it would leave those releases unmatched - but corrects both the clock and
        /// <c>_isGamePaused</c>. The flag is not cosmetic: <c>EntityRigidbody.HandleMovement</c> returns
        /// early while it is set, for every body in the world. With only the clock restored, a player on
        /// the pause menu left every creature on their machine running in place and still swinging - and
        /// the host's creatures are the ones the teammate sees move. The next check after the session
        /// ends recomputes it from the list.
        /// </summary>
        private static void CheckIsGamePausedPostfix(GameState_TimeScale __instance)
        {
            if (!CoopSession.IsLive) return;

            __instance._isGamePaused = false;
            if (Time.timeScale == 0f) Time.timeScale = 1f;
        }
    }
}
