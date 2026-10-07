using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Coop;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Session
{
    /// <summary>
    /// What must match for two worlds to be the same: the savegame mode (Insane changes spawns and AI; Immersive
    /// and Hardcore change saving and dying), and every mod setting shared with <see cref="CoopAccess.ShareHostSetting"/>.
    /// The host's values win; a client in another mode leaves.
    /// </summary>
    internal static class HostSettings
    {
        private static readonly Dictionary<string, string> Sent = new();
        private static readonly HashSet<string> UnknownLogged = new();

        private static float _sincePoll;
        private static bool _reloadPending;

        /// <summary>The host's savegame mode, from the session rules.</summary>
        internal static byte HostGameMode { get; set; } = SessionRulesMessage.UnknownGameMode;

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
        internal static void Tick(float unscaledDeltaTime)
        {
            if (CoopSession.State == CoopSessionState.Joined)
            {
                CheckGameMode();

                // A frame after the batch arrived, so one reload covers every setting.
                if (_reloadPending && Core.InGameplay && SavegameGameHandler.TryGet(out SavegameGameHandler handler) && handler != null)
                {
                    _reloadPending = false;
                    MelonLogger.Msg("[Coop] the host's world settings differ from this save's; loading it again with them.");
                    World.GameOver.LoadLatest(handler);
                }

                return;
            }

            if (CoopSession.State != CoopSessionState.Hosting) return;

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
        internal static void Take(HostSettingMessage message)
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
        internal static void Clear()
        {
            Sent.Clear();
            UnknownLogged.Clear();
            _reloadPending = false;
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

        private static void CheckGameMode()
        {
            byte local = LocalGameMode();
            if (HostGameMode == SessionRulesMessage.UnknownGameMode || local == SessionRulesMessage.UnknownGameMode) return;
            if (local == HostGameMode) return;

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
    }
}
