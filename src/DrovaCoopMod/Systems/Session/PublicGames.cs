using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// Games hosted over Steam and listed in a public Steam lobby, so players who are not friends find and join them.
    /// The lobby carries the same <see cref="LanGameInfo"/> as LAN discovery.
    /// </summary>
    internal static class PublicGames
    {
        private const string InfoKey = "info";
        private const string ProtocolKey = "protocol";
        private const string ModeKey = "mode";
        private const string ArenaValue = "arena";
        private const float RefreshSeconds = 2f;

        private static readonly List<SteamLobby> Lobbies = new();

        private static bool _listed;
        private static bool _joinedPublic;
        private static bool _expectArena;
        private static float _sinceRefresh;
        private static string? _info;

        /// <summary>Lists the Steam session just started. The API closes the lobby when the session stops.</summary>
        internal static void List(bool arena)
        {
            SteamAccess.OpenLobby();
            SteamAccess.SetLobbyData(ProtocolKey, CoopProtocol.Version.ToString());
            SteamAccess.SetLobbyData(ModeKey, arena ? ArenaValue : "together");

            _listed = true;
            _sinceRefresh = RefreshSeconds;
            _info = null;
        }

        internal static void Unlist()
        {
            _listed = false;
            _joinedPublic = false;
            _expectArena = false;
            _info = null;
        }

        /// <summary>Whether the game being joined was listed as arena only, so the handshake must say the same.</summary>
        internal static bool ExpectsArena => _joinedPublic && _expectArena;

        /// <summary>Joins a game from the public list. Returns the line to show the player.</summary>
        /// <param name="host">The host's Steam id.</param>
        /// <param name="arena">Whether the listing said arena only.</param>
        internal static string Join(ulong host, bool arena)
        {
            string line = CoopActions.JoinSteam(host);
            _joinedPublic = CoopSession.State != CoopSessionState.Failed;
            _expectArena = arena;
            return line;
        }

        /// <summary>
        /// Whether this machine plays a Hardcore hero in a game joined from the public list. A stranger's host decides
        /// over creatures and the end of the run, and in Hardcore that end is final.
        /// </summary>
        internal static bool RefusesHardcore() => _joinedPublic && Checkpoint.Rules == GameMode.Hardcore;

        /// <summary>Whether the listing's player count is due for a refresh.</summary>
        internal static bool Due(float unscaledDeltaTime)
        {
            if (!_listed) return false;

            _sinceRefresh += unscaledDeltaTime;
            if (_sinceRefresh < RefreshSeconds) return false;

            _sinceRefresh = 0f;

            // A Hardcore death is final for the whole party, and a stranger could claim one.
            if (Checkpoint.Rules == GameMode.Hardcore)
            {
                SteamAccess.CloseLobby();
                Unlist();
                MelonLogger.Warning("[Coop] Hardcore runs are not listed publicly; the public listing was closed. Only friends can join now.");
                return false;
            }

            return true;
        }

        /// <summary>Updates what searchers see. Steam is only told when it changed.</summary>
        internal static void Describe(byte[] lanInfo)
        {
            string info = Convert.ToBase64String(lanInfo);
            if (info == _info) return;

            _info = info;
            SteamAccess.SetLobbyData(InfoKey, info);
        }

        /// <summary>Searches for public games of this protocol. The result arrives through <see cref="SteamAccess.OnLobbiesFound"/>.</summary>
        internal static void Search()
        {
            SteamAccess.FindLobbies((ProtocolKey, CoopProtocol.Version.ToString()));
        }

        /// <summary>The last search's games. Lobbies that do not describe themselves are skipped.</summary>
        internal static List<(ulong Host, LanGameInfo Info, bool Arena)> Found()
        {
            SteamAccess.GetFoundLobbies(Lobbies);

            List<(ulong, LanGameInfo, bool)> games = new();
            foreach (SteamLobby lobby in Lobbies)
            {
                if (!lobby.Data.TryGetValue(InfoKey, out string? encoded) || !TryDecode(encoded, out LanGameInfo info)) continue;

                games.Add((lobby.HostId, info, lobby.Data.TryGetValue(ModeKey, out string? mode) && mode == ArenaValue));
            }

            return games;
        }

        private static bool TryDecode(string encoded, out LanGameInfo info)
        {
            info = default;
            byte[] bytes = new byte[encoded.Length];
            return Convert.TryFromBase64String(encoded, bytes, out int length) && LanGameInfo.TryDecode(bytes[..length], out info);
        }
    }
}
