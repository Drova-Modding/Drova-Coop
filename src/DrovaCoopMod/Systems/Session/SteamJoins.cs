using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    /// <summary>
    /// Joins a Steam friend's game from Steam's "Join Game" or an accepted invite. A session needs a world, so a request
    /// made in the main menu, or the one Steam put on the command line when it started the game, waits for a loaded save.
    /// </summary>
    internal sealed class SteamJoins : ICoopSystem
    {
        private static bool _listening;
        private static ulong _pendingHost;

        /// <summary>Join requests taken from Steam. For tests.</summary>
        internal static int Requested { get; private set; }

        /// <summary>Called from the main menu as well, so a request there is heard and kept.</summary>
        internal static void Listen()
        {
            if (_listening || !SteamAccess.IsAvailable) return;

            _listening = true;
            SteamAccess.OnJoinRequested += OnJoinRequested;

            if (SteamJoin.TryParse(Environment.GetCommandLineArgs(), out ulong host)) Request(host);
        }

        private static void OnJoinRequested(string joinString)
        {
            if (SteamJoin.TryParse(joinString, out ulong host)) Request(host);
        }

        private static void Request(ulong host)
        {
            Requested++;
            _pendingHost = host;
            MelonLogger.Msg(Core.InGameplay
                ? "[Coop] Joining a Steam friend's game..."
                : "[Coop] A Steam friend's game will be joined once you load a save.");
        }

        /// <summary>Only runs in a world, which is where a pending join can start.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (_pendingHost == 0) return;

            ulong host = _pendingHost;
            _pendingHost = 0;
            MelonLogger.Msg("[Coop] " + CoopActions.JoinSteam(host));
        }
    }
}
