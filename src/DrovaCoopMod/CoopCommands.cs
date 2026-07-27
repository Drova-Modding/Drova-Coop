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
        private static bool _registered;
        private static bool _warned;

        /// <summary>
        /// Registers every coop command, if the game is far enough along to accept them.
        ///
        /// Call this on every scene load until it returns true. Registering reaches for
        /// <c>CheatGameHandler</c>, and that manager does not exist while the game is still bootstrapping
        /// - asking too early throws out of IL2CPP rather than returning empty-handed, so there is nothing
        /// to test beforehand and retrying is the only way to find the moment it works. Which scene that
        /// is, is not worth pinning down: it is a property of the game's startup, not of this mod.
        ///
        /// Cheat mode itself does not have to be on. The API queues commands and registers them when it
        /// is switched on, so a queued command counts as success here.
        /// </summary>
        /// <returns>True once the commands are registered and the caller can stop retrying.</returns>
        internal static bool TryRegister()
        {
            if (_registered) return true;

            try
            {
                CheatMenuAccess.RegisterCheat("coop_host", OnHost, 0, 0,
                    "Host a coop session through the configured relay.", "coop_host");

                CheatMenuAccess.RegisterCheat("coop_join", OnJoin, 0, 1,
                    "Join a coop session. Without an argument, uses the configured session code.", "coop_join [code]");

                CheatMenuAccess.RegisterCheat("coop_leave", OnLeave, 0, 0,
                    "Leave the current coop session.", "coop_leave");

                CheatMenuAccess.RegisterCheat("coop_status", OnStatus, 0, 0,
                    "Show the session state, the code others need, and who is in it.", "coop_status");

                _registered = true;
                MelonLogger.Msg("[Coop] Console commands ready: coop_host, coop_join, coop_leave, coop_status.");
                return true;
            }
            catch (Exception e)
            {
                // Once, not per scene. This is expected to fail on the scenes that load before the game
                // managers do, and an error line for each of those buries the one that matters.
                if (!_warned)
                {
                    _warned = true;
                    MelonLogger.Warning("[Coop] Console commands not registerable yet, will retry on the next scene: " + e.Message);
                }

                return false;
            }
        }

        private static void OnHost(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                CoopSession.HostViaRelay(
                    CoopPreferences.RelayAddress,
                    CoopPreferences.RelayPort,
                    CoopPreferences.SessionCodeOrNull(),
                    CoopPreferences.PasswordOrNull());

                Say($"hosting through {CoopPreferences.RelayAddress}:{CoopPreferences.RelayPort}; run coop_status for the code.");
            });
        }

        private static void OnJoin(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                // The typed argument wins over the configured code, because somebody typing a code into a
                // console has just been given one and does not expect to edit a preferences file first.
                string? code = args != null && args.Length > 0 ? args[0].String : CoopPreferences.SessionCodeOrNull();

                if (string.IsNullOrWhiteSpace(code))
                {
                    Say("no session code. Use coop_join <code>, or set DrovaCoop.SessionCode.");
                    return;
                }

                CoopSession.JoinViaRelay(
                    CoopPreferences.RelayAddress,
                    CoopPreferences.RelayPort,
                    code!.Trim(),
                    CoopPreferences.PasswordOrNull());

                Say($"joining '{code.Trim()}' through {CoopPreferences.RelayAddress}:{CoopPreferences.RelayPort}...");
            });
        }

        private static void OnLeave(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                CoopSession.Leave();
                Say("left the session.");
            });
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
