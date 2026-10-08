using Il2CppDrova;
using Il2CppDrova.GUI;
using MelonLoader;

namespace DrovaCoopMod.UI
{
    /// <summary>The game's location-name overlay, the one on-screen line the mod uses for news.</summary>
    internal static class Overlay
    {
        /// <summary>Shows the text. A missing or failing overlay only costs the line.</summary>
        internal static void Show(string text)
        {
            try
            {
                if (!GUIGameHandler.TryGet(out GUIGameHandler gui) || gui == null) return;

                GUI_OverlayTextHandler overlay = gui.GetOverlayTextHandler();
                if (overlay != null) overlay.CreateOverlayText(text);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Coop] showing '" + text + "' failed: " + e.Message);
            }
        }
    }
}
