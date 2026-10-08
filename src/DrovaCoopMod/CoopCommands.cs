using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppCommandTerminal;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod
{
    /// <summary>
    /// The console commands that drive and diagnose a session.
    /// </summary>
    internal static partial class CoopCommands
    {
        /// <summary>
        /// Registers every coop command. Once, at melon init. The API queues commands registered before the terminal exists.
        /// </summary>
        internal static void Register()
        {
            CheatMenuAccess.RegisterCheat("coop_host", OnHost, 0, 0,
                "Host a coop session through the configured relay.", "coop_host");

            CheatMenuAccess.RegisterCheat("coop_join", OnJoin, 0, 1,
                "Join a coop session. Without an argument, uses the configured session code.", "coop_join [code]");

            CheatMenuAccess.RegisterCheat("coop_leave", OnLeave, 0, 0,
                "Leave the current coop session.", "coop_leave");

            CheatMenuAccess.RegisterCheat("coop_window", OnWindow, 0, 0,
                "Open or close the session window: type a code, host, join, read the code to hand on.", "coop_window");

            CheatMenuAccess.RegisterCheat("coop_status", OnStatus, 0, 0,
                "Show the session state, the code others need, and who is in it.", "coop_status");

            CheatMenuAccess.RegisterCheat("coop_revive", OnRevive, 0, 1,
                "Host only: revive every dead creature near this player, in the world and in the save. " +
                "Optional range in units, default 300.",
                "coop_revive [range]");

            CheatMenuAccess.RegisterCheat("coop_arena", OnArena, 0, 1,
                "Host only: mark a circle around you where players can hurt each other whatever friendly fire says. " +
                "Optional radius in units (2-2000), default 2000; 'off' takes it down.",
                "coop_arena [radius|off]");

            CheatMenuAccess.RegisterCheat("coop_checkpoint", _ => Run(() => Say(GameOver.BackToCheckpoint())), 0, 0,
                "Host only: send everyone back to the party checkpoint - the last save all of you wrote at once.",
                "coop_checkpoint");

            CheatMenuAccess.RegisterCheat("coop_trade", args => Run(() => Say(Trade.Open(Arg(args, 0)))), 0, 1,
                "Trade with the nearest teammate (or the one whose name starts with the argument) in the merchant window. " +
                "They see what you offer and want, and accept or refuse.",
                "coop_trade [name]");

            CheatMenuAccess.RegisterCheat("coop_team", args => Run(() =>
            {
                string arg = Arg(args, 0) ?? string.Empty;
                byte team = byte.TryParse(arg, out byte parsed) ? parsed : (byte)0;
                Arena.SetTeam(CoopSession.LocalPlayerId, team);
                Say(team == 0 ? "you fight for no team." : $"you fight for team {team}.");
            }), 0, 1,
                "Pick your arena team (1-250); no number or 'off' leaves it. Teammates cannot hurt each other in the arena.",
                "coop_team [n|off]");

            CheatMenuAccess.RegisterCheat("coop_tp", OnTeleport, 0, 1,
                "Teleport to a teammate, by name or player id. Without an argument, to the only " +
                "other player.",
                "coop_tp [player]");

            CheatMenuAccess.RegisterCheat("coop_resync", OnResync, 0, 1,
                "Ask the host for the world's variables and journal again. The repair for a world that " +
                "has fallen behind - after loading a savegame, or when a quest stage did not cross. " +
                "With the argument 'deep' it also re-runs the consequences of values this world already " +
                "holds, which is what repairs a save written by a build that stored them silently.",
                "coop_resync [deep]");

            CheatMenuAccess.RegisterCheat("coop_backups", OnBackups, 0, 0,
                "List the copies of save slots this mod kept before the game overwrote them, and where " +
                "they are.",
                "coop_backups");

            CheatMenuAccess.RegisterCheat("coop_saves", OnSaves, 0, 0,
                "List the save slots a co-op session has written into. Those slots hold world state other " +
                "players' worlds produced, which is not undoable; coop_backups has the copies from before.",
                "coop_saves");

            CheatMenuAccess.RegisterCheat("coop_restore", OnRestore, 2, 3,
                "Main menu only: put a backup back into a save slot. Hero and slot are the numbers in the " +
                "folder names; the third argument is 'latest' (default), 'session' for the newest copy " +
                "from before a session began, or a backup folder name as coop_backups prints it. The " +
                "slot as it is now is copied first, so a restore can be undone.",
                "coop_restore <hero> <slot> [latest|session|name]");

            CheatMenuAccess.RegisterCheat("coop_gvars", OnGVarReport, 0, 1,
                "List global variables that differ from their initial value, with an optional name filter. " +
                "For seeing what a world actually holds.",
                "coop_gvars [filter]");

            CheatMenuAccess.RegisterCheat("coop_gvar_trace", OnGVarTrace, 0, 1,
                "Log every variable this machine asserts whose name contains the given text. Without an " +
                "argument, stops tracing.",
                "coop_gvar_trace [filter]");

            CheatMenuAccess.RegisterCheat("coop_work", OnWork, 0, 1,
                "Report whether shared mining is working: how many hooks fired, how many spots this " +
                "world can address, what is waiting for its chunk. 'off' stops replaying teammates' " +
                "swings for the rest of the session, 'on' resumes.",
                "coop_work [on|off]");

            CheatMenuAccess.RegisterCheat("coop_haul", OnHaul, 0, 1,
                "Report whether shared rubble hauling is working: how many piles this world named, how " +
                "many crossed, what is waiting. 'off' stops sharing the haul for the rest of the " +
                "session, 'on' resumes, 'repair' puts back piles a teammate carried off and never " +
                "dumped, 'ping' proves the round trip without walking to the mine.",
                "coop_haul [on|off|repair|ping]");

            CheatMenuAccess.RegisterCheat("coop_clock", OnClock, 0, 1,
                "Report whether this world is allowed to move its own day, and how often it has tried. " +
                "Only the host's clock turns the world over; 'off' lets this machine advance its own " +
                "day again, which on a client stages this world for tomorrow and then rewinds it.",
                "coop_clock [on|off]");

            CheatMenuAccess.RegisterCheat("coop_gvar_derived", OnGVarDerived, 0, 0,
                "List the global variables this world has worked out are its own tally of replicated " +
                "work, and therefore never copied from a teammate.",
                "coop_gvar_derived");

            CheatMenuAccess.RegisterCheat("coop_gvar_quiet", OnGVarQuiet, 0, 1,
                "Stop running the consequences of variables that arrive from teammates - values still " +
                "land, the world just does not react. The kill switch, for salvaging a session in which " +
                "reacting does harm. 'off' turns it back on.",
                "coop_gvar_quiet [on|off]");

#if DEBUG
            CheatMenuAccess.RegisterCheat("coop_guids", OnGuids, 0, 1,
                "Report which creatures can still be found by the guid they report. Optional range in units.",
                "coop_guids [range]");
#endif

            MelonLogger.Msg("[Coop] Console commands ready: coop_host, coop_join, coop_leave, coop_window, coop_status, " +
                "coop_tp, coop_trade, coop_team, coop_arena, coop_checkpoint, coop_revive, coop_resync, coop_backups, coop_saves, " +
                "coop_restore. Diagnostics: coop_gvars, coop_gvar_trace, coop_gvar_quiet, coop_gvar_derived, coop_work, coop_haul, " +
                "coop_clock."
#if DEBUG
                + " coop_guids."
#endif
                );
        }

        private static void OnHost(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() => Say(CoopActions.Host()));
        }

        private static void OnJoin(Il2CppReferenceArray<CommandArg> args)
        {
            // A typed code wins. CoopActions falls back to the configured one.
            Run(() => Say(CoopActions.Join(Arg(args, 0))));
        }

        private static void OnLeave(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() => Say(CoopActions.Leave()));
        }

        private static void OnWindow(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() => Say(UI.SessionWindow.Toggle()));
        }

#if DEBUG
        /// <summary>
        /// Reports which creatures can still be found by the guid they report. See <see cref="Diagnostics.GuidReport"/>.
        /// </summary>
        private static void OnGuids(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                // Not CommandArg.Float, which logs its own complaint and returns zero on bad text.
                float.TryParse(Arg(args, 0), out float range);

                Diagnostics.GuidReport.Run(range, Say);
            });
        }
#endif

        private static void OnArena(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                string arg = Arg(args, 0) ?? string.Empty;
                if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    Say(Arena.Clear());
                    return;
                }

                float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float radius);
                Say(Arena.Mark(radius));
            });
        }

        /// <summary>
        /// Revives every dead creature near the host's player, in the world and the save, to repair a
        /// poisoned save. It cannot tell a camp from a story corpse, so the range is small.
        /// </summary>
        private static void OnRevive(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                if (CoopSession.State != CoopSessionState.Hosting)
                {
                    Say("Only a host can revive. Its save is the world every other machine follows.");
                    return;
                }

                float.TryParse(Arg(args, 0), out float range);

                if (range <= 0f)
                {
                    range = 300f;
                }

                if (!PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self))
                {
                    Say("No player to measure from.");
                    return;
                }

                if (!Il2CppDrova.EntityGameHandler.TryGet(out Il2CppDrova.EntityGameHandler entities)
                    || entities == null)
                {
                    Say("The world is not ready.");
                    return;
                }

                Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppDrova.Utilities.LazyLoading.LazyActor>
                    handles = entities._lazyActorGuidDictionary;

                if (handles == null)
                {
                    Say("The world is not ready.");
                    return;
                }

                Vector2 origin = self.GetFeetPosition();
                float squared = range * range;
                List<string> revived = new();

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Il2CppDrova.Utilities.LazyLoading.LazyActor> pair
                    in handles)
                {
                    Il2CppDrova.Utilities.LazyLoading.LazyActor lazy = pair.Value;
                    if (lazy == null || lazy.IsDestroyed) continue;

                    string guid = pair.Key;
                    if (string.IsNullOrEmpty(guid)) continue;

                    // A runtime-minted guid names nothing in the save and nothing on another machine.
                    if (ActorSpawnAccess.IsRuntimeCreated(guid)) continue;

                    if ((lazy.Position - origin).sqrMagnitude > squared) continue;
                    if (!lazy.IsDead()) continue;

                    EnemyRegistry.ForceAlive(guid);
                    RemoteEnemyDeath.ForgetEnemyDeath(guid);
                    EnemyRebirth.AllowAgain(guid);
                    EnemyRebirth.Request(guid);
                    revived.Add(guid);
                }

                // Everybody connected rebuilds the same corpses now, not on their next join.
                HostWorldState.AnnounceEnemyRevive(revived);

                Say(revived.Count == 0
                    ? $"Nothing dead within {range:0} units."
                    : $"{revived.Count} creature(s) within {range:0} units called alive again, here and on " +
                      "every connected machine. The next autosave makes it permanent. This cannot tell a " +
                      "camp from a story corpse, so run it standing in the camp.");
            });
        }

        /// <summary>
        /// Teleports this player to a teammate's body, matched by id or any part of the name. Refuses on ambiguity.
        /// </summary>
        private static void OnTeleport(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                if (!CoopSession.IsLive)
                {
                    Say("Not in a session.");
                    return;
                }

                string? wanted = Arg(args, 0);
                bool wantedIsId = byte.TryParse(wanted, out byte wantedId);

                List<KeyValuePair<PlayerId, string>> matches = new();

                foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
                {
                    if (player.Key == CoopSession.LocalPlayerId) continue;

                    bool hit = string.IsNullOrEmpty(wanted)
                        || (wantedIsId && player.Key.Value == wantedId)
                        || (player.Value != null && player.Value.Contains(wanted, StringComparison.OrdinalIgnoreCase));

                    if (hit)
                    {
                        matches.Add(player);
                    }
                }

                if (matches.Count == 0)
                {
                    Say(string.IsNullOrEmpty(wanted)
                        ? "Nobody else is in the session."
                        : $"No other player matches '{wanted}'.");
                    return;
                }

                if (matches.Count > 1)
                {
                    string names = string.Join(", ", matches.Select(match => $"{match.Key}: {match.Value}"));

                    Say($"More than one player matches: {names}. Say which.");
                    return;
                }

                PlayerId target = matches[0].Key;
                string name = matches[0].Value;

                if (!RemotePlayers.Bodies.TryGetValue(target, out RemotePlayer? remote)
                    || remote?.Body == null || remote.Body.IsDestroyed)
                {
                    Say($"{name} has no body in this world yet. Wait a moment and try again.");
                    return;
                }

                if (!PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self))
                {
                    Say("No player to teleport.");
                    return;
                }

                Vector2 destination = remote.Body.GetFeetPosition();
                float distance = (destination - self.GetFeetPosition()).magnitude;

                self.TeleportTo(destination, portWithGroup: false, updateCam: true);

                Say($"Teleported to {name}, {distance:0} units from where you stood.");
            });
        }

        /// <summary>
        /// Asks the host for the world's variables and journal again.
        /// </summary>
        private static void OnResync(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                if (CoopSession.State != CoopSessionState.Joined)
                {
                    Say(CoopSession.State == CoopSessionState.Hosting
                        ? "This machine is the host - its world is the one everybody else catches up to."
                        : "Not in a session.");
                    return;
                }

                bool deep = string.Equals(Arg(args, 0), "deep", StringComparison.OrdinalIgnoreCase);

                RemoteGVar.RequestResync(GVarResyncRequestMessage.ReasonManual, deep);

                Say(deep
                    ? "Asked the host for the whole world, and for the consequences of values this " +
                      "world already holds to be run again. Watch the log for the catch-up line."
                    : "Asked the host for the whole world. Watch the log for the catch-up line.");
            });
        }

        private static void OnBackups(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                Say($"backups: {SaveBackups.Root}; hook {(SaveBackups.Hooked ? "applied" : "NOT APPLIED")}, " +
                    $"{(SaveBackups.HookFired ? "fired" : "never fired")}; {SaveBackups.Made} written this run, " +
                    $"{SaveBackups.Failed} failed; keeping {CoopPreferences.SaveBackupsToKeep} recent per slot.");

                List<string> lines = SaveBackups.Describe();

                if (lines.Count == 0)
                {
                    Say("no backups yet. One is taken every time the game overwrites a slot.");
                    return;
                }

                foreach (string line in lines)
                {
                    Say(line);
                }
            });
        }

        private static void OnSaves(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                Say($"coop saves: {CoopSaveMarks.Root}; {CoopSaveMarks.Marked} slot(s) marked this run, " +
                    $"{CoopSaveMarks.Warnings} played alone after a session.");

                List<string> lines = CoopSaveMarks.Describe();

                if (lines.Count == 0)
                {
                    Say("no slot has been in a session yet. A slot is marked the first time a session saves it.");
                    return;
                }

                foreach (string line in lines)
                {
                    Say(line);
                }
            });
        }

        private static void OnRestore(Il2CppReferenceArray<CommandArg>? args)
        {
            Run(() =>
            {
                if (!int.TryParse(Arg(args, 0), out int hero) || !int.TryParse(Arg(args, 1), out int slot)
                    || hero < 0 || slot < 0)
                {
                    Say("Usage: coop_restore <hero> <slot> [latest|session|name]. Hero and slot are the numbers in Hero_NN/Savegame_NN.");
                    return;
                }

                string which = Arg(args, 2) is { } named && !string.IsNullOrWhiteSpace(named) ? named : "latest";

                SaveBackups.TryRestore(hero, slot, which, out string message);
                Say(message);
            });
        }

        /// <summary>
        /// Lists the global variables that have moved from their initial values.
        /// </summary>
        private static void OnGVarReport(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                string filter = Arg(args, 0) ?? string.Empty;

                GVarSnapshot.Report(filter, Say);
            });
        }

        /// <summary>
        /// Turns the assertion trace on or off.
        /// </summary>
        private static void OnGVarTrace(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                string filter = Arg(args, 0) ?? string.Empty;

                GVarWatch.Trace = filter;

                Say(string.IsNullOrEmpty(filter)
                    ? "No longer tracing variable assertions."
                    : $"Tracing every variable this machine asserts whose name contains '{filter}'.");
            });
        }

        /// <summary>
        /// Reports the clock guard, or turns it on or off. Works outside a session.
        /// </summary>
        private static void OnClock(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                if (OnOff(Arg(args, 0)) is bool on)
                {
                    ClockGuard.Enabled = on;

                    Say(ClockGuard.Enabled
                        ? "Only the host's clock turns the world over again."
                        : "This machine may advance its own day again. On a client that fires every " +
                          "routine port and every day-forward flag in this world alone, sends those " +
                          "flags to the host, and is then pulled back with no way to undo any of it.");

                    return;
                }

                SayClock();
            });
        }

        /// <summary>
        /// Reports the clock guard and how this world has followed the host's day.
        /// </summary>
        private static void SayClock()
        {
            Say($"clock guard {(ClockGuard.Hooked ? "installed" : "NOT INSTALLED")}, " +
                $"{(ClockGuard.Enabled ? "on" : "OFF")}.");

            Say($"{ClockGuard.Refused} local day advance(s) refused here " +
                $"(largest {ClockGuard.LargestRefusedHours:0.#} game hours).");

            Say($"{ClockGuard.Asked} of those were sleeps the host was asked to run; this " +
                $"world slept for a teammate {DaytimeSync.Granted} time(s) and turned " +
                $"{DaytimeSync.RefusedAsks} ask(s) down.");

            Say($"{DaytimeSync.FollowedSleeps} host sleep(s) followed, " +
                $"{DaytimeSync.Rewinds} time(s) this world was found ahead and pulled back " +
                $"(largest {DaytimeSync.LargestRewindHours:0.#} h). Any of those after the " +
                "guard is installed names a clock writer it missed.");
        }

        private static void OnHaul(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                string? arg = Arg(args, 0);

                if (OnOff(arg) is bool on)
                {
                    HaulShare.Enabled = on;

                    Say(HaulShare.Enabled
                        ? "Hauling is shared again."
                        : "Hauling is no longer shared. Each player carries their own world's piles " +
                          "for the rest of this session, which is what the game does on its own.");

                    return;
                }

                if (string.Equals(arg, "repair", StringComparison.OrdinalIgnoreCase))
                {
                    int repaid = HaulShare.RepayOrphans();

                    Say(repaid == 0
                        ? "No teammate is holding a pile this world has given up. Nothing to put back."
                        : $"Put {repaid} pile(s) back where a teammate carried them off; this world " +
                          "can haul them itself now.");

                    return;
                }

                if (string.Equals(arg, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    HaulShare.Ping();

                    Say("Sent one pile no spot owns. Every teammate should log a refusal within a " +
                        "frame - that refusal is the rule the whole design rests on.");

                    return;
                }

                foreach (string line in HaulShare.Describe())
                {
                    Say(line);
                }

                if (HaulShare.HooksInstalled < 3 || HaulShare.SpawnsSeen == 0)
                {
                    Say("Mine one rock that drops rubble and run this again. If the spawn count stays " +
                        "at zero the game inlined the funnel away and nothing else here means anything.");
                }
            });
        }

        private static void OnWork(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                if (OnOff(Arg(args, 0)) is bool on)
                {
                    ResourceWork.Enabled = on;

                    Say(ResourceWork.Enabled
                        ? "Teammates' swings are replayed here again."
                        : "Teammates' swings will no longer be replayed here. Mining goes back to " +
                          "being per-machine for the rest of this session.");

                    return;
                }

                SayWork();
            });
        }

        /// <summary>
        /// Reports the shared-mining subsystem. Hooks below 5, or zero swings after a real one, means the funnel was inlined.
        /// </summary>
        private static void SayWork()
        {
            Say($"[Work] hooks {ResourceWork.HooksInstalled}/5 " +
                $"(swings seen {ResourceWork.MinigameHits}, spots registered " +
                $"{ResourceWork.RegisterHits}, of those {ResourceWork.PlacedByPosition} " +
                $"named by position and {ResourceWork.PlacedLate} of those only when asked), " +
                "derived-var hook " +
                (GVarDerived.Hooked ? "yes" : "NO"));

            Say($"[Work] {ResourceWork.Registered} spot(s) addressable here, " +
                $"{ResourceWork.Pending} waiting for their chunk; " +
                $"{ResourceWork.Worked} swing(s) told to the session, " +
                $"{ResourceWork.Applied} applied from it, " +
                $"{ResourceWork.Settled} settled without acting them out, " +
                $"{ResourceWork.AlreadyThere} already that far, " +
                $"{ResourceWork.Deferred} deferred, {ResourceWork.Refused} refused." +
                (ResourceWork.Enabled ? "" : " REPLAY IS OFF (coop_work off)."));

            Say($"[Work] spot table: {ResourceWork.SnapshotSent} sent, " +
                $"{ResourceWork.SnapshotReceived} received. " +
                $"{GVarDerived.Count} variable(s) found to be this world's own tally, " +
                $"{GVarDerived.RefusedAbsolutes} teammate total(s) refused because of it.");

            if (ResourceWork.HooksInstalled < 5 || ResourceWork.MinigameHits == 0)
            {
                Say("Mine one node and run this again. If the swing count stays at zero the game " +
                    "inlined the funnel away and nothing else here means anything.");
            }
        }

        /// <summary>
        /// Lists the variables discovered to be this world's own tally.
        /// </summary>
        private static void OnGVarDerived(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                int listed = 0;

                foreach (string name in GVarDerived.Names)
                {
                    Say("  " + name);
                    listed++;
                }

                Say(listed == 0
                    ? "No variable has been found to be a tally yet. They are discovered when work " +
                      "is done, so mine something first."
                    : $"{listed} variable(s) are this world's own count and are never copied from a " +
                      "teammate.");
            });
        }

        /// <summary>
        /// The kill switch for running the consequences of a teammate's variables.
        /// </summary>
        private static void OnGVarQuiet(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                bool quiet = !string.Equals(Arg(args, 0), "off", StringComparison.OrdinalIgnoreCase);

                RemoteGVar.Quiet = quiet;

                Say(quiet
                    ? "Values from teammates will land, but this world will not react to them. Quest " +
                      "stages driven by a teammate stop advancing here; this is a salvage mode."
                    : "This world reacts to teammates' variables again.");
            });
        }

        /// <summary>
        /// The command's argument at <paramref name="index"/>, or null when it was not given.
        /// </summary>
        private static string? Arg(Il2CppReferenceArray<CommandArg>? args, int index)
        {
            return args != null && args.Length > index ? args[index].String : null;
        }

        /// <summary>
        /// Reads an <c>on</c> or <c>off</c> argument, in any case. Null for anything else.
        /// </summary>
        private static bool? OnOff(string? arg)
        {
            if (string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)) return false;

            return null;
        }

        /// <summary>
        /// Runs a command body, turning anything it throws into a console line, since the console swallows exceptions.
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
        /// Writes to the console and the log.
        /// </summary>
        private static void Say(string message)
        {
            // So a test runner's "run coop_status" can read the answer instead of scraping a log.
            Drova_Modding_API.Systems.Testing.TestAgent.Report(message);
            MelonLogger.Msg("[Coop] " + message);

            try
            {
                Terminal.Log("[coop] " + message);
            }
            catch (Exception)
            {
                // The console may not be up yet. The log line above is already written.
            }
        }
    }
}
