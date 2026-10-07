using Drova_Modding_API;
using Il2CppNodeCanvas.DialogueTrees.UI;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Drops dialogue windows whose speaker's body was unloaded mid-conversation, which would otherwise throw
    /// inside the window manager and hang the conversation.
    /// </summary>
    internal static class DialogueWindowGuard
    {
        private static int _dropped;

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The three ways a line reaches the per-actor window dictionary.
            Hooking.TryPrefix(harmony, typeof(DS_DialogueWindowManager), nameof(DS_DialogueWindowManager.RegisterNewWindow),
                typeof(DialogueWindowGuard), nameof(PrunePrefix));
            Hooking.TryPrefix(harmony, typeof(DS_DialogueWindowManager), nameof(DS_DialogueWindowManager.CheckWindowActive),
                typeof(DialogueWindowGuard), nameof(PrunePrefix));
            Hooking.TryPrefix(harmony, typeof(DS_DialogueWindowManager), nameof(DS_DialogueWindowManager.UpdatePositioning),
                typeof(DialogueWindowGuard), nameof(PrunePrefix));
        }

        private static void PrunePrefix(DS_DialogueWindowManager __instance)
        {
            try
            {
                Il2CppSystem.Collections.Generic.Dictionary<Il2CppDrova.Actor, DS_DialogueWindowControl> windows = __instance._windows;
                if (windows == null || windows.Count == 0) return;

                List<Il2CppDrova.Actor> dead = new();
                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<Il2CppDrova.Actor, DS_DialogueWindowControl> pair in windows)
                {
                    // A control without a CurrentWindow is still alive (faded out between lines), so it stays.
                    if (pair.Key == null || pair.Value == null) dead.Add(pair.Key!);
                }

                foreach (Il2CppDrova.Actor actor in dead)
                {
                    windows.Remove(actor);
                    _dropped++;
                }

                if (dead.Count > 0 && _dropped == dead.Count)
                {
                    MelonLogger.Msg("[Dialogue] a speaker's dialogue window went with its body mid-conversation; the dead " +
                        "window was dropped so the next line builds a new one.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Dialogue] checking the dialogue windows failed: " + e);
            }
        }
    }
}
