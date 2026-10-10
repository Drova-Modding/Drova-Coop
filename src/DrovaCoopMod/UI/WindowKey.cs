using Drova_Modding_API.Access;
using Drova_Modding_API.Register;
using Drova_Modding_API.UI.Builder;
using Il2CppCustomFramework.Localization;
using MelonLoader;
using UnityEngine.InputSystem;

namespace DrovaCoopMod.UI
{
    /// <summary>
    /// The key that opens the session window, rebindable on the mod's page in the game's options menu.
    /// </summary>
    internal static class WindowKey
    {
        private const string ActionName = "CoopWindow";
        private const string ModName = "DrovaCoop";
        private const string KeyboardGroup = "Keyboard;Mouse";

        // F8: Drova binds F2, F5, F9 and F11, and UnityExplorer and Archipelago take F7.
        private const string DefaultPath = "<Keyboard>/f8";

        // The registered action sits in the gameplay map, which is off in menus and while the window is open.
        // This one is always on and listens on the registered action's key.
        private static InputAction? _listener;
        private static bool _localized;

        /// <summary>The control the window listens on, for tests.</summary>
        internal static string Bound => _listener?.bindings[0].effectivePath ?? "none";

        /// <summary>Whether the key was let go this frame.</summary>
        internal static bool Pressed => _listener != null && _listener.WasReleasedThisFrame();

        internal static void Initialize()
        {
            InputActionRegister.OnInputActionsLoaded += Register;
            if (InputActionRegister.Instance != null) Register();

            OptionMenuAccess.Instance.OnOptionMenuOpen += BuildPanel;
            OptionMenuAccess.Instance.OnOptionMenuClose += Listen;
        }

        private static void Register()
        {
            InputActionRegister.Instance.AddActionToGameplayMap(ActionName)?.AddBinding(DefaultPath, null, null, KeyboardGroup);
            Listen();
        }

        private static void Listen()
        {
            try
            {
                InputAction registered = InputActionRegister.Instance[ActionName];
                int index = registered.GetBindingIndex(InputBinding.MaskByGroup(KeyboardGroup));
                string path = index >= 0 ? registered.bindings[index].effectivePath : DefaultPath;

                _listener?.Disable();
                _listener = new InputAction(ActionName, InputActionType.Button, path);
                _listener.Enable();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Coop] the session window key could not be bound; coop_window still opens it. " + e.Message);
            }
        }

        private static void BuildPanel()
        {
            if (!_localized)
            {
                _localized = true;
                LocalizationAccess.CreateLocalizationEntries([
                    new LocalizationAccess.LocalizationEntry("Title", "Drova Coop", LocalizationDB.ELanguage.en),
                    new LocalizationAccess.LocalizationEntry("Window", "Session window", LocalizationDB.ELanguage.en),
                    new LocalizationAccess.LocalizationEntry("Title", "Drova Coop", LocalizationDB.ELanguage.de),
                    new LocalizationAccess.LocalizationEntry("Window", "Sitzungsfenster", LocalizationDB.ELanguage.de),
                ], ModName);
            }

            OptionUIBuilder? builder = OptionMenuAccess.Instance.GetBuilder(ModName);
            builder?.CreateTitle(LocalizationAccess.GetLocalizedString(ModName, "Title"))
                .CreateInputActionSection([new(LocalizationAccess.GetLocalizedString(ModName, "Window"), ActionName)])
                .Build();
        }
    }
}
