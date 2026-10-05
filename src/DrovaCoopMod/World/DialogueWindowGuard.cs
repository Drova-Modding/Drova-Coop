using Drova_Modding_API;
using Il2CppNodeCanvas.DialogueTrees.UI;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Drops dialogue windows whose speaker has gone, before the window manager reaches for them.
    ///
    /// Each speaker's window is created as a child of that speaker's body and kept in a per-actor dictionary that the
    /// game only clears when a conversation ends. A speaker whose body is unloaded or rebuilt mid-conversation takes
    /// its window with it, the entry stays, and the next line that speaker says throws inside the window manager -
    /// the conversation hangs and the player's dialogue window is broken until it happens to be torn down. Rare alone;
    /// in a session bodies come and go far more often. With the dead entry gone, the manager simply builds a new window.
    /// </summary>
    internal static class DialogueWindowGuard
    {
        /// <summary>How many dead windows were dropped. For the log.</summary>
        private static int _dropped;

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The three ways into the dictionary a line takes.
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
                    // Unity's null: a destroyed object compares equal to null. A control without a CurrentWindow is alive:
                    // its window faded out between that speaker's lines, and SetupWindow builds the next one. Dropping
                    // it made the manager build a second control on the same speaker, and the text of the orphan threw.
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
