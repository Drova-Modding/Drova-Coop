using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Coop;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session.Patches;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// What must match for two worlds to be the same: the savegame mode and every mod setting shared with
    /// <see cref="CoopAccess.ShareHostSetting"/>. The host's values win. A client whose mode differs only in insane
    /// plays the host's while joined, without its save being rewritten. A client in another mode leaves.
    /// </summary>
    internal sealed class HostSettings : ICoopSystem
    {
        private static readonly Dictionary<string, string> Sent = new();
        private static readonly HashSet<string> UnknownLogged = new();

        private static float _sincePoll;
        private static bool _reloadPending;

        // Once per join and per local load: a mode switched in the options mid-session differs for a moment on every machine.
        private static bool _modeChecked;

        // Insane picks a scene's spawn groups when it wakes, so a world loaded before following the host needs one reload.
        private static bool _followed;
        private static bool _worldLoadedFollowing;

        /// <summary>The host's savegame mode, from the session rules.</summary>
        internal static byte HostGameMode { get; set; } = SessionRulesMessage.UnknownGameMode;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(Il2Cpp.SavegameDataExtensions), nameof(Il2Cpp.SavegameDataExtensions.GetGameMode),
                typeof(HostSettingsPatches), nameof(HostSettingsPatches.GetGameModePostfix));
        }

        /// <summary>Client: the host's mode in place of the save's when the two differ only in insane.</summary>
        internal static GameMode FollowHostMode(GameMode own)
        {
            if (!CoopSession.JoinedWorld || HostGameMode == SessionRulesMessage.UnknownGameMode) return own;

            GameMode host = (GameMode)HostGameMode;
            if (host == own || WithoutInsane(host) != WithoutInsane(own)) return own;

            _followed = true;
            return host;
        }

        private static GameMode WithoutInsane(GameMode mode) => mode switch
        {
            GameMode.Insane_Classic => GameMode.Default,
            GameMode.Insane_Immersive => GameMode.Immersive,
            GameMode.Insane_Hardcore => GameMode.Hardcore,
            _ => mode,
        };

        /// <summary>This machine's savegame mode, or <see cref="SessionRulesMessage.UnknownGameMode"/> with none loaded.</summary>
        internal static byte LocalGameMode()
        {
            return DifficultyAccess.TryGetGameMode(out GameMode mode) ? (byte)mode : SessionRulesMessage.UnknownGameMode;
        }

        /// <summary>Host: every shared setting to a client that was just accepted.</summary>
        internal static void SendAll(INetPeer peer)
        {
            foreach (HostSetting setting in CoopAccess.HostSettings)
            {
                NetworkAccess.Send(peer, new HostSettingMessage { Key = setting.Key, Value = Read(setting) });
            }
        }

        /// <summary>Host: sends what changed. Client: refuses a mismatched mode and reloads once for enforced settings.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (CoopSession.JoinedWorld)
            {
                CheckGameMode();

                // A frame after the batch arrived, so one reload covers every setting. Never into a load still running
                // (a game-over load landing): that load never finishes and the screen stays black.
                if (_reloadPending && Core.InGameplay && !Il2CppDrova.SceneGameHandler.IsLoadingScreenActive
                    && Il2CppDrova.LoadingScreenHandles.LoadingScreenHandler.Instance?.IsWorldReady() == true
                    && SavegameGameHandler.TryGet(out SavegameGameHandler handler) && handler != null)
                {
                    _reloadPending = false;
                    MelonLogger.Msg("[Coop] the host's world settings differ from this save's; loading it again with them.");
                    GameOver.LoadLatest(handler);
                }

                return;
            }

            // Not while loading: a mod's value is empty between the old world and the new one, and sending that reloads every client.
            if (!CoopSession.HostsWorld || !Core.InGameplay) return;

            // Polled: mod settings have no change event.
            _sincePoll += unscaledDeltaTime;
            if (_sincePoll < 1f) return;
            _sincePoll = 0f;

            foreach (HostSetting setting in CoopAccess.HostSettings)
            {
                string value = Read(setting);
                if (Sent.TryGetValue(setting.Key, out string? last) && last == value) continue;

                Sent[setting.Key] = value;
                NetworkAccess.SendToAll(new HostSettingMessage { Key = setting.Key, Value = value });
            }
        }

        /// <summary>Client: enforces the host's value of one setting.</summary>
        private static void Take(HostSettingMessage message)
        {
            HostSetting? setting = CoopAccess.HostSettings.FirstOrDefault(s => s.Key == message.Key);
            if (setting == null)
            {
                if (UnknownLogged.Add(message.Key))
                {
                    MelonLogger.Warning($"[Coop] the host enforces '{message.Key}', which no mod here knows; " +
                        "install the host's mods or the worlds will differ.");
                }

                return;
            }

            if (Read(setting) == message.Value) return;

            try
            {
                setting.Enforce(message.Value);
                MelonLogger.Msg($"[Coop] '{message.Key}' follows the host: {message.Value}.");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Coop] enforcing the host's '{message.Key}' failed: {e}");
                return;
            }

            // Out of a world the next load uses it anyway.
            if (setting.NeedsReload && Core.InGameplay) _reloadPending = true;
        }

        /// <summary>The session ended: every player gets their own settings back.</summary>
        void ICoopSystem.EndSession()
        {
            Sent.Clear();
            UnknownLogged.Clear();
            _reloadPending = false;
            _modeChecked = false;
            _followed = false;
            _worldLoadedFollowing = false;
            HostGameMode = SessionRulesMessage.UnknownGameMode;

            foreach (HostSetting setting in CoopAccess.HostSettings)
            {
                try
                {
                    setting.Enforce(null);
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"[Coop] giving back '{setting.Key}' failed: {e}");
                }
            }
        }

        /// <summary>A savegame was loaded here: its mode is checked against the host's again.</summary>
        internal static void SaveLoaded()
        {
            _modeChecked = false;
            _worldLoadedFollowing = CoopSession.JoinedWorld && HostGameMode != SessionRulesMessage.UnknownGameMode;
        }

        private static void CheckGameMode()
        {
            if (_modeChecked) return;

            byte local = LocalGameMode();
            if (HostGameMode == SessionRulesMessage.UnknownGameMode || local == SessionRulesMessage.UnknownGameMode) return;

            _modeChecked = true;
            if (local == HostGameMode)
            {
                if (_followed && !_worldLoadedFollowing)
                {
                    MelonLogger.Msg($"[Coop] this save plays the host's {(GameMode)HostGameMode} while joined; loading it again so its monsters match.");
                    _reloadPending = true;
                }

                return;
            }

            CoopSession.FailWith($"The host plays {(GameMode)HostGameMode}, this save is {(GameMode)local}. " +
                "Both must play the same mode: it decides spawns, saving and dying. Load a save in the host's mode and join again.");
        }

        private static string Read(HostSetting setting)
        {
            try
            {
                return setting.Read() ?? string.Empty;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[Coop] reading '{setting.Key}' failed: {e}");
                return string.Empty;
            }
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<HostSettingMessage>(MessageIds.HostSetting, (_, m) =>
            {
                if (CoopSession.IsFromHost()) Take(m);
            });
        }
    }
}
