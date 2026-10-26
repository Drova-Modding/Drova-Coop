using Drova_Modding_API;
using DrovaCoopMod.Systems.Players.Patches;
using Il2CppDrova.Poise;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// A remote player's body is stunned when its owner is, and only then. Blows landing on it here would otherwise drain
    /// its poise on their own schedule, a stagger its owner never had, while the owner's real one never showed.
    /// </summary>
    internal sealed class PuppetPoise : ICoopSystem
    {
        string? ICoopSystem.HookSwitch => HookSwitches.PuppetPoise;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, HarmonyLib.AccessTools.Method(typeof(PoiseController), nameof(PoiseController.ChangePoise), [typeof(float), typeof(bool)]),
                typeof(PuppetPoisePatches), nameof(PuppetPoisePatches.ChangePoisePrefix), "PoiseController.ChangePoise(float,bool)");

            // Every blow calls this one, which does not reach the other through the hook.
            Hooking.TryPrefix(harmony, HarmonyLib.AccessTools.Method(typeof(PoiseController), nameof(PoiseController.ChangePoise), [typeof(float)]),
                typeof(PuppetPoisePatches), nameof(PuppetPoisePatches.ChangePoiseUnforcedPrefix), "PoiseController.ChangePoise(float)");
        }

        /// <summary>Refuses a puppet's unforced poise loss. The mod's own breaks are forced, as the game's guard break is.</summary>
        internal static bool ChangePoisePrefix(PoiseController __instance, float delta, bool force)
        {
            try
            {
                return force || delta >= 0f || __instance == null || __instance.Owner == null || !RemotePlayers.IsPuppet(__instance.Owner);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Poise] judging a poise change failed: " + e);
                return true;
            }
        }

        /// <summary>
        /// Breaks a body's poise so its own poise-break behaviours play the stagger or full break and end it on their timer.
        /// False when it was already broken.
        /// </summary>
        internal static bool Break(PoiseController? poise)
        {
            if (poise == null || poise.TryGetPoiseBreak(out _)) return false;

            // Forced past the temporary immunity the last break left. Never zero: the breaks ignore a rise.
            poise.ChangePoise(0f - Mathf.Max(poise.CurValue, 1f), true);
            return true;
        }
    }
}
