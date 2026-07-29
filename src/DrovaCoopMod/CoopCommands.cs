using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppCommandTerminal;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod
{
    /// <summary>
    /// The console commands that drive a session, until there is a connection menu worth putting in front
    /// of a player.
    ///
    /// Commands rather than hotkeys because hotkeys have no conflict check and this mod has already lost
    /// three of them to other mods and to the game. A command cannot collide with anything, says what it
    /// did, and can carry an argument - a session code is not something a keypress can express.
    /// </summary>
    internal static class CoopCommands
    {
        /// <summary>
        /// Registers every coop command. Once, at melon init.
        ///
        /// No retry loop and no try/catch, because the API guarantees neither is needed: registering
        /// before the terminal exists queues the command, and the queue is flushed when cheat mode comes
        /// on or the next scene loads. Cheat mode does not have to be on when this runs.
        ///
        /// This used to be called on every scene load until it stopped throwing, back when asking too
        /// early came out of IL2CPP as an exception. Both ends of that workaround are gone.
        /// </summary>
        internal static void Register()
        {
            CheatMenuAccess.RegisterCheat("coop_host", OnHost, 0, 0,
                "Host a coop session through the configured relay.", "coop_host");

            CheatMenuAccess.RegisterCheat("coop_join", OnJoin, 0, 1,
                "Join a coop session. Without an argument, uses the configured session code.", "coop_join [code]");

            CheatMenuAccess.RegisterCheat("coop_leave", OnLeave, 0, 0,
                "Leave the current coop session.", "coop_leave");

            CheatMenuAccess.RegisterCheat("coop_status", OnStatus, 0, 0,
                "Show the session state, the code others need, and who is in it.", "coop_status");

            MelonLogger.Msg("[Coop] Console commands ready: coop_host, coop_join, coop_leave, coop_status.");
        }

        private static void OnHost(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() => Say(CoopActions.Host()));
        }

        private static void OnJoin(Il2CppReferenceArray<CommandArg> args)
        {
            // The typed argument wins over the configured code, because somebody typing a code into a
            // console has just been given one and does not expect to edit a preferences file first. That
            // preference is what CoopActions falls back to when nothing was typed.
            Run(() => Say(CoopActions.Join(args != null && args.Length > 0 ? args[0].String : null)));
        }

        private static void OnLeave(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() => Say(CoopActions.Leave()));
        }

        private static void OnStatus(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                Say($"state: {CoopSession.State}, you are {CoopSession.LocalPlayerId}");

                if (CoopSession.LastError != null)
                {
                    Say("last error: " + CoopSession.LastError);
                }

                if (CoopSession.SessionCode != null)
                {
                    // Whether the relay can read the session is not a detail: without a password the
                    // operator sees everything, and a player deserves to know which of the two they are in.
                    Say($"code: {CoopSession.SessionCode} ({(CoopSession.IsEncrypted ? "encrypted" : "NOT encrypted, the relay can read this session")})");
                }

                if (CoopSession.Players.Count == 0)
                {
                    Say("nobody in session.");
                    return;
                }

                // Only worth a line once it has happened. A non-zero count means somebody in this session
                // is sending positions nobody can be put at, which is either a broken build or a peer
                // doing it deliberately - and this is the only place either becomes visible.
                if (CoopSession.ImplausibleStates > 0)
                {
                    Say($"refused {CoopSession.ImplausibleStates} state updates carrying impossible numbers.");
                }

                foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
                {
                    if (player.Key == CoopSession.LocalPlayerId)
                    {
                        Say($"  {player.Key}: {player.Value} (you)");
                        continue;
                    }

                    // Measured end to end, not read off the transport: through a relay the transport's own
                    // ping is the time to the relay, which sits between the two players rather than at
                    // either end of the link that matters.
                    float? roundTrip = CoopSession.RoundTripMs(player.Key);
                    string latency = roundTrip.HasValue ? $"{roundTrip.Value:0} ms" : "measuring";

                    Say($"  {player.Key}: {player.Value} ({latency})");
                    SayWhereTheyStand(player.Key);
                }
            });
        }

        /// <summary>
        /// Reports how far away another player's body is and whether the world exists around it.
        ///
        /// Distance and chunk state together, because either alone says nothing: a body twenty units away
        /// is in the local player's own chunks whatever its reference point does, and a chunk that reads
        /// as loaded proves nothing until the two players are far enough apart that only the remote body
        /// could be holding it up. Walk apart, run this, and the answer is unambiguous.
        /// </summary>
        private static void SayWhereTheyStand(PlayerId player)
        {
            if (!World.RemotePlayers.Bodies.TryGetValue(player, out World.RemotePlayer? body) || body == null) return;
            if (body.Body == null) return;

            string distance = PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor local) && local != null
                ? $"{Vector3.Distance(local.transform.position, body.Body.transform.position):0} units away"
                : "distance unknown";

            Say($"    {distance}, {body.DescribeStreaming()}");
        }

        /// <summary>
        /// Runs a command body, turning anything it throws into a console line.
        ///
        /// The console swallows exceptions from a command and leaves the player looking at a prompt that
        /// did nothing, which is indistinguishable from the command not existing.
        /// </summary>
        private static void Run(Action body)
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                Say("failed: " + e.Message);
                MelonLogger.Error("[Coop] console command failed: " + e);
            }
        }

        /// <summary>
        /// Writes to the console and the log. Both, because the console is what the player is looking at
        /// and the log is what gets pasted into a bug report.
        /// </summary>
        private static void Say(string message)
        {
            MelonLogger.Msg("[Coop] " + message);

            try
            {
                Terminal.Log("[coop] " + message);
            }
            catch (Exception)
            {
                // The console may not be up yet; the log line above has already been written.
            }
        }
    }
}
