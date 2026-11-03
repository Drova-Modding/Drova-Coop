using System.Net.NetworkInformation;
using System.Net.Sockets;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.World;
using Il2CppDrova;
using UnityEngine;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// Host, join and leave in one place for the console, options panel and session window.
    /// Each returns the line to show the player.
    /// </summary>
    internal static class CoopActions
    {
        private static readonly List<DiscoveredHost> Discovered = new();

        /// <summary>Starts hosting directly on a UDP port, for the local network or a forwarded port.</summary>
        /// <param name="port">Null uses the configured LAN port.</param>
        internal static string HostDirect(int? port = null)
        {
            string? refusal = VersionCheck.Refusal;
            if (refusal != null) return refusal;

            int listen = port ?? CoopPreferences.LanPort;
            CoopSession.HostDirect(listen);

            if (CoopSession.State == CoopSessionState.Failed) return CoopSession.LastError ?? "hosting failed.";

            return $"hosting on UDP port {listen}. On your network: {AddressLine(listen)}.";
        }

        /// <summary>Joins a direct host by <c>ip</c> or <c>ip:port</c>. The port defaults to the configured LAN port.</summary>
        internal static string JoinDirect(string? address)
        {
            if (!LanGameInfo.TryParseAddress(address, CoopPreferences.LanPort, out string host, out int port))
            {
                return "give the host's address as ip or ip:port.";
            }

            string? refusal = VersionCheck.Refusal;
            if (refusal != null) return refusal;

            CoopSession.JoinDirect(host, port);
            return $"joining {host}:{port} directly...";
        }

        /// <summary>Hosts over Steam: friends join from Steam, or by invite.</summary>
        internal static string HostSteam()
        {
            string? refusal = VersionCheck.Refusal;
            if (refusal != null) return refusal;

            CoopSession.HostViaSteam();

            if (CoopSession.State == CoopSessionState.Failed) return CoopSession.LastError ?? "hosting failed.";

            return "hosting over Steam. Invite friends, or they pick Join Game on you in Steam.";
        }

        /// <summary>Joins a Steam friend's game.</summary>
        internal static string JoinSteam(ulong hostSteamId)
        {
            string? refusal = VersionCheck.Refusal;
            if (refusal != null) return refusal;

            CoopSession.JoinViaSteam(hostSteamId);

            if (CoopSession.State == CoopSessionState.Failed) return CoopSession.LastError ?? "joining failed.";

            return "joining over Steam...";
        }

        /// <summary>Opens Steam's invite dialog for the session being hosted over Steam.</summary>
        internal static string InviteSteam()
        {
            if (!CoopSession.ViaSteam || CoopSession.State != CoopSessionState.Hosting) return "host over Steam first.";

            return SteamAccess.OpenInviteDialog(SteamJoin.Format(SteamAccess.LocalId))
                ? "pick friends in the Steam overlay. If it does not open, enable the Steam overlay for Drova."
                : "Steam is not running.";
        }

        /// <summary>Joins a game found on the local network.</summary>
        internal static string JoinFound(DiscoveredHost found, LanGameInfo info)
        {
            if (!info.IsJoinable(CoopProtocol.Version)) return $"{info.HostName} cannot be joined: {Describe(info)}.";

            string? refusal = VersionCheck.Refusal;
            if (refusal != null) return refusal;

            CoopSession.JoinDirect(found.Address, found.Port);
            return $"joining {info.HostName} at {found.Address}:{found.Port}...";
        }

        /// <summary>Starts or stops looking for games on the local network. Cheap to call every frame.</summary>
        internal static void Browse(bool on)
        {
            if (on == NetworkAccess.IsDiscovering) return;

            if (on) NetworkAccess.StartDiscovery(CoopPreferences.LanPort);
            else NetworkAccess.StopDiscovery();
        }

        /// <summary>The games found so far, with what they said about themselves. Answers that are not ours are skipped.</summary>
        internal static List<(DiscoveredHost Found, LanGameInfo Info)> FoundGames()
        {
            NetworkAccess.GetDiscoveredHosts(Discovered);

            List<(DiscoveredHost, LanGameInfo)> games = new();
            foreach (DiscoveredHost found in Discovered)
            {
                if (LanGameInfo.TryDecode(found.Payload, out LanGameInfo info)) games.Add((found, info));
            }

            return games;
        }

        /// <summary>One line about a found game: players, and why it cannot be joined.</summary>
        internal static string Describe(LanGameInfo info)
        {
            string line = $"{info.Players}/{info.MaxPlayers}";
            if (info.ProtocolVersion != CoopProtocol.Version) return line + $", protocol {info.ProtocolVersion} (you have {CoopProtocol.Version})";

            return info.Players >= info.MaxPlayers ? line + ", full" : line;
        }

        /// <summary>This machine's IPv4 addresses with the port, for a friend to type in.</summary>
        internal static string AddressLine(int port)
        {
            try
            {
                string[] addresses = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => $"{a.Address}:{port}")
                    .ToArray();

                return addresses.Length > 0 ? string.Join(", ", addresses) : "no network address found";
            }
            catch (NetworkInformationException)
            {
                return "the network addresses could not be read";
            }
        }

        /// <summary>Teleports the local player to a teammate's body.</summary>
        internal static string GoTo(PlayerId target)
        {
            string name = CoopSession.NameOf(target);

            if (!RemotePlayers.Bodies.TryGetValue(target, out RemotePlayer? remote) || remote?.Body == null || remote.Body.IsDestroyed)
            {
                return $"{name} has no body in this world yet. Wait a moment and try again.";
            }

            if (!PlayerAccess.TryGetPlayer(out Actor self)) return "No player to teleport.";

            Vector2 destination = remote.Body.GetFeetPosition();
            float distance = (destination - self.GetFeetPosition()).magnitude;
            self.TeleportTo(destination, portWithGroup: false, updateCam: true);

            return $"Teleported to {name}, {distance:0} units from where you stood.";
        }

        /// <summary>Asks the host for the world's variables and journal again. Client only.</summary>
        /// <param name="deep">Also re-run the consequences of values this world already holds.</param>
        internal static string CatchUp(bool deep)
        {
            if (CoopSession.State != CoopSessionState.Joined)
            {
                return CoopSession.State == CoopSessionState.Hosting
                    ? "This machine is the host - its world is the one everybody else catches up to."
                    : "Not in a session.";
            }

            RemoteGVar.RequestResync(GVarResyncRequestMessage.ReasonManual, deep);

            return deep
                ? "Asked the host for the whole world, and for the consequences of values this " +
                  "world already holds to be run again. Watch the log for the catch-up line."
                : "Asked the host for the whole world. Watch the log for the catch-up line.";
        }

        /// <summary>Safe when there is none.</summary>
        internal static string Leave()
        {
            CoopSession.Leave();
            return "left the session.";
        }
    }
}
