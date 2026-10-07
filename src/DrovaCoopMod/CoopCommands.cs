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
    /// The console commands that drive and diagnose a session.
    /// </summary>
    internal static class CoopCommands
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

            CheatMenuAccess.RegisterCheat("coop_checkpoint", _ => Run(() => Say(World.GameOver.BackToCheckpoint())), 0, 0,
                "Host only: send everyone back to the party checkpoint - the last save all of you wrote at once.",
                "coop_checkpoint");

            CheatMenuAccess.RegisterCheat("coop_trade", args => Run(() => Say(World.Trade.Open(Arg(args, 0)))), 0, 1,
                "Trade with the nearest teammate (or the one whose name starts with the argument) in the merchant window. " +
                "They see what you offer and want, and accept or refuse.",
                "coop_trade [name]");

            CheatMenuAccess.RegisterCheat("coop_team", args => Run(() =>
            {
                string arg = Arg(args, 0) ?? string.Empty;
                byte team = byte.TryParse(arg, out byte parsed) ? parsed : (byte)0;
                World.Arena.SetTeam(CoopSession.LocalPlayerId, team);
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
                "coop_tp, coop_revive, coop_resync, coop_backups, coop_saves, coop_arena, coop_restore. Diagnostics: coop_gvars, " +
                "coop_gvar_trace, coop_gvar_quiet."
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

        /// <summary>
        /// Revives every dead creature near the host's player, in the world and the save, to repair a
        /// poisoned save. It cannot tell a camp from a story corpse, so the range is small.
        /// </summary>
        private static void OnArena(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                string arg = Arg(args, 0) ?? string.Empty;
                if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    Say(World.Arena.Clear());
                    return;
                }

                float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float radius);
                Say(World.Arena.Mark(radius));
            });
        }

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

                    if (((Vector2)lazy.Position - origin).sqrMagnitude > squared) continue;
                    if (!lazy.IsDead()) continue;

                    World.EnemyRegistry.ForceAlive(guid);
                    CoopSession.ForgetEnemyDeath(guid);
                    World.EnemyRebirth.AllowAgain(guid);
                    World.EnemyRebirth.Request(guid);
                    revived.Add(guid);
                }

                // Everybody connected rebuilds the same corpses now, not on their next join.
                CoopSession.AnnounceEnemyRevive(revived);

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

                if (!World.RemotePlayers.Bodies.TryGetValue(target, out World.RemotePlayer? remote)
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

                CoopSession.RequestResync(GVarResyncRequestMessage.ReasonManual, deep);

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

        private static void OnRestore(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                if (args == null || args.Length < 2
                    || !int.TryParse(args[0].String, out int hero) || !int.TryParse(args[1].String, out int slot)
                    || hero < 0 || slot < 0)
                {
                    Say("Usage: coop_restore <hero> <slot> [latest|session|name]. Hero and slot are the numbers in Hero_NN/Savegame_NN.");
                    return;
                }

                string which = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2].String) ? args[2].String : "latest";

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

                World.GVarSnapshot.Report(filter, Say);
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

                World.GVarWatch.Trace = filter;

                Say(string.IsNullOrEmpty(filter)
                    ? "No longer tracing variable assertions."
                    : $"Tracing every variable this machine asserts whose name contains '{filter}'.");
            });
        }

        /// <summary>
        /// Reports the shared-mining subsystem, and switches replaying off when it misbehaves. Works outside a session.
        /// </summary>
        private static void OnClock(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                if (OnOff(Arg(args, 0)) is bool on)
                {
                    World.ClockGuard.Enabled = on;

                    Say(World.ClockGuard.Enabled
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
            Say($"clock guard {(World.ClockGuard.Hooked ? "installed" : "NOT INSTALLED")}, " +
                $"{(World.ClockGuard.Enabled ? "on" : "OFF")}.");

            Say($"{World.ClockGuard.Refused} local day advance(s) refused here " +
                $"(largest {World.ClockGuard.LargestRefusedHours:0.#} game hours).");

            Say($"{World.ClockGuard.Asked} of those were sleeps the host was asked to run; this " +
                $"world slept for a teammate {World.DaytimeSync.Granted} time(s) and turned " +
                $"{World.DaytimeSync.RefusedAsks} ask(s) down.");

            Say($"{World.DaytimeSync.FollowedSleeps} host sleep(s) followed, " +
                $"{World.DaytimeSync.Rewinds} time(s) this world was found ahead and pulled back " +
                $"(largest {World.DaytimeSync.LargestRewindHours:0.#} h). Any of those after the " +
                "guard is installed names a clock writer it missed.");
        }

        private static void OnHaul(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                string? arg = Arg(args, 0);

                if (OnOff(arg) is bool on)
                {
                    World.HaulShare.Enabled = on;

                    Say(World.HaulShare.Enabled
                        ? "Hauling is shared again."
                        : "Hauling is no longer shared. Each player carries their own world's piles " +
                          "for the rest of this session, which is what the game does on its own.");

                    return;
                }

                if (string.Equals(arg, "repair", StringComparison.OrdinalIgnoreCase))
                {
                    int repaid = World.HaulShare.RepayOrphans();

                    Say(repaid == 0
                        ? "No teammate is holding a pile this world has given up. Nothing to put back."
                        : $"Put {repaid} pile(s) back where a teammate carried them off; this world " +
                          "can haul them itself now.");

                    return;
                }

                if (string.Equals(arg, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    World.HaulShare.Ping();

                    Say("Sent one pile no spot owns. Every teammate should log a refusal within a " +
                        "frame - that refusal is the rule the whole design rests on.");

                    return;
                }

                foreach (string line in World.HaulShare.Describe())
                {
                    Say(line);
                }

                if (World.HaulShare.HooksInstalled < 3 || World.HaulShare.SpawnsSeen == 0)
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
                    World.ResourceWork.Enabled = on;

                    Say(World.ResourceWork.Enabled
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
            Say($"[Work] hooks {World.ResourceWork.HooksInstalled}/5 " +
                $"(swings seen {World.ResourceWork.MinigameHits}, spots registered " +
                $"{World.ResourceWork.RegisterHits}, of those {World.ResourceWork.PlacedByPosition} " +
                $"named by position and {World.ResourceWork.PlacedLate} of those only when asked), " +
                "derived-var hook " +
                (World.GVarDerived.Hooked ? "yes" : "NO"));

            Say($"[Work] {World.ResourceWork.Registered} spot(s) addressable here, " +
                $"{World.ResourceWork.Pending} waiting for their chunk; " +
                $"{World.ResourceWork.Worked} swing(s) told to the session, " +
                $"{World.ResourceWork.Applied} applied from it, " +
                $"{World.ResourceWork.Settled} settled without acting them out, " +
                $"{World.ResourceWork.AlreadyThere} already that far, " +
                $"{World.ResourceWork.Deferred} deferred, {World.ResourceWork.Refused} refused." +
                (World.ResourceWork.Enabled ? "" : " REPLAY IS OFF (coop_work off)."));

            Say($"[Work] spot table: {World.ResourceWork.SnapshotSent} sent, " +
                $"{World.ResourceWork.SnapshotReceived} received. " +
                $"{World.GVarDerived.Count} variable(s) found to be this world's own tally, " +
                $"{World.GVarDerived.RefusedAbsolutes} teammate total(s) refused because of it.");

            if (World.ResourceWork.HooksInstalled < 5 || World.ResourceWork.MinigameHits == 0)
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

                foreach (string name in World.GVarDerived.Names)
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

                World.RemoteGVar.Quiet = quiet;

                Say(quiet
                    ? "Values from teammates will land, but this world will not react to them. Quest " +
                      "stages driven by a teammate stop advancing here; this is a salvage mode."
                    : "This world reacts to teammates' variables again.");
            });
        }

        private static void OnStatus(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                Say($"state: {CoopSession.State}, you are {CoopSession.LocalPlayerId}");
                Say($"build {Versioning.VersionPolicy.Format(VersionCheck.Running)}; version policy: {VersionCheck.Summary}");

                if (CoopSession.LastError != null)
                {
                    Say("last error: " + CoopSession.LastError);
                }

                if (CoopSession.SessionCode != null)
                {
                    // Without a password the relay operator can read everything.
                    Say($"code: {CoopSession.SessionCode} ({(CoopSession.IsEncrypted ? "encrypted" : "NOT encrypted, the relay can read this session")})");
                }

                Say("patches off: " + HookSwitches.Disabled);

                // Some fire only in rare scenes; one that never fires through a scene it belongs to is a dead hook.
                IReadOnlyList<string> silent = Drova_Modding_API.Hooking.NeverFired();
                if (silent.Count > 0) Say($"{silent.Count} patches never fired: " + string.Join(", ", silent));

                if (CoopSession.Players.Count == 0)
                {
                    Say("nobody in session.");
                    return;
                }

                // Non-zero means a peer sends impossible positions, a broken build or a deliberate one.
                if (CoopSession.ImplausibleStates > 0)
                {
                    Say($"refused {CoopSession.ImplausibleStates} state updates carrying impossible numbers.");
                }

                // A pending count that never falls means a guid that names nothing here, such as a runtime spawn.
                if (CoopSession.KnownDeadEnemies > 0 || World.RemoteEnemyDeath.Pending > 0)
                {
                    Say($"{CoopSession.KnownDeadEnemies} creatures known dead, " +
                        $"{World.RemoteEnemyDeath.Pending} waiting for a body to apply it to, " +
                        $"{World.RemoteEnemyDeath.Credited} unloaded kill(s) counted by this world's quest observers.");
                }

                // Both are normally non-zero: runtime spawns, and story drift between the players.
                if (World.EnemyDeathWatch.Unnameable > 0)
                {
                    Say($"{World.EnemyDeathWatch.Unnameable} local deaths could not be shared: spawned at " +
                        "runtime, so no other machine can name them.");
                }

                // Climbing with no spawns announced means a mod bypasses the API's spawn path.
                if (World.EnemyDeathWatch.SpawnedElsewhere > 0 || World.SpawnedEnemies.Count > 0
                    || World.SpawnedEnemies.Pending > 0)
                {
                    Say($"{World.SpawnedEnemies.Count} spawned creatures shared, " +
                        $"{World.SpawnedEnemies.Pending} noticed and not shared yet, " +
                        $"{World.SpawnedEnemies.Unbuildable} announced but unbuildable here, " +
                        $"{World.EnemyDeathWatch.SpawnedElsewhere} deaths skipped as spawned-elsewhere.");
                }

                // Failure here means two independent summons rather than a missing one.
                if (World.SummonWatch.Shared > 0 || World.SummonWatch.Paired > 0 || World.SummonWatch.Unnamed > 0)
                {
                    Say($"conjure hook: {(World.SummonWatch.HookFired ? "yes" : "no")}; " +
                        $"{World.SummonWatch.Shared} summons claimed, {World.SummonWatch.Paired} paired here, " +
                        $"{World.SummonWatch.Unnamed} left local (a creature's own summon, not a player's).");
                }

                // "exp hook: no" means the players level apart.
                if (World.DamageWatch.KeptAlive > 0)
                {
                    Say($"{World.DamageWatch.KeptAlive} times a remote player's body was held back from " +
                        "dying here. Their own game decides that; a corpse here could never stand up.");
                }

                // "hook: no" with the rule on means creatures fight at normal health.
                if (CoopSession.EnemyHealthPercent != CoopProtocol.DefaultEnemyHealthPercent || World.EnemyScaling.Scaled > 0)
                {
                    Say($"enemy health {CoopSession.EnemyHealthPercent}%; scaling hook: " +
                        $"{(World.EnemyScaling.Hooked ? "applied" : "NOT APPLIED")}, " +
                        $"{(World.EnemyScaling.HookFired ? "fired" : "never fired")}; " +
                        $"{World.EnemyScaling.Scaled} blows scaled here.");
                }

                if (CoopSession.LootMode == CoopProtocol.LootModeShared || World.LootShare.Known > 0)
                {
                    Say($"loot {(CoopSession.LootMode == CoopProtocol.LootModeShared ? "shared" : "per player")}; " +
                        $"hooks {World.LootShare.HooksInstalled} of 6; {World.LootShare.Addressable} loaded here under a " +
                        $"shared name; {World.LootShare.Proposed} chest(s) offered, {World.LootShare.ProposalsRefused} refused here; " +
                        $"{World.LootShare.Sent} take(s) sent, {World.LootShare.Applied} applied, " +
                        $"{World.LootShare.Held} waiting for their object; {World.LootShare.Known} on the ledger; " +
                        $"{World.LootShare.BodiesNamed} bodies named, {World.LootShare.BodiesWaiting} waiting for a creature.");

                    Say($"caches: hooks {World.CacheShare.HooksInstalled} of 3; " +
                        $"{World.CacheShare.Addressable} loaded here under a shared name, " +
                        $"{World.CacheShare.Spilled} spilled pickup(s) kept out of the ledger; " +
                        $"{World.CacheShare.Sent} sent, {World.CacheShare.Applied} applied, " +
                        $"{World.CacheShare.Refused} offers refused here.");
                }

                if (World.Arena.Active)
                {
                    Say($"arena: marked at {World.Arena.Center.x:0.#},{World.Arena.Center.y:0.#}, " +
                        $"{World.Arena.Radius:0.#} units; round {World.Arena.Round}, " +
                        $"{World.Arena.Rounds} called; you are {(World.Arena.HoldsLocal() ? "inside" : "outside")}, team {World.Arena.TeamOf(CoopSession.LocalPlayerId)}; {World.Arena.DescribeScore()}.");
                }
                else if (World.Arena.TeamOf(CoopSession.LocalPlayerId) is byte team and not 0)
                {
                    Say($"arena: none marked; you fight for team {team}.");
                }

                Say($"block verdicts: {(World.BlockVerdicts.Hooked ? "puppet guards refused" : "puppet guards DECIDE LOCALLY")}; " +
                    $"{World.BlockVerdicts.Suppressed} local decision(s) refused, {World.BlockVerdicts.Sent} verdict(s) sent, " +
                    $"{World.BlockVerdicts.Applied} guard break(s) played out.");

                Say($"world memory: {(World.LedgerStore.Loaded ? "read" : "not read yet")}; " +
                    $"{World.LedgerStore.PickupsRead} pickup(s) and {World.LedgerStore.ObjectsRead} object(s) " +
                    $"remembered from earlier sessions, {World.LedgerStore.Written} written at the last save.");

                // Both ride the backups' save prefix, so one switch turns off all three.
                if (!SaveBackups.Hooked)
                {
                    Say("the save hook is not applied, so slots are not marked and this world remembers " +
                        "nothing across a restart either. Check the 'backups' hook switch.");
                }

                Say($"coop saves: {CoopSaveMarks.Marked} slot(s) marked this run, " +
                    $"{CoopSaveMarks.Warnings} played alone after a session; marks under UserData/{CoopSaveMarks.FolderName}.");

                Say($"game mode: {World.Checkpoint.Mode}; party checkpoint {(World.Checkpoint.Committed == 0 ? "none yet" : World.Checkpoint.Committed.ToString())}" +
                    (World.Checkpoint.AllowsGoingBack ? "." : " (going back by choice is off in this mode)."));
                Say($"world objects: hooks {World.WorldObjects.HooksInstalled} of {World.WorldObjects.HookCount}; " +
                    $"{World.WorldObjects.Addressable} loaded here under a shared name; " +
                    $"{World.WorldObjects.Sent} change(s) sent, {World.WorldObjects.Applied} applied, " +
                    $"{World.WorldObjects.Held} waiting for their object, {World.WorldObjects.Abandoned} given up on; {World.WorldObjects.Known} on the ledger" +
                    $"{(CoopSession.State == CoopSessionState.Joined && !World.WorldObjects.SnapshotComplete ? "; join snapshot still arriving" : string.Empty)}.");

                Say($"guard reach: {(World.GuardReach.Hooked ? "patched" : "NOT patched")}; " +
                    $"{World.GuardReach.Entered} crime volume(s) entered by a teammate, {World.GuardReach.Left} left, " +
                    $"{World.GuardReach.Holding} held now, {World.GuardReach.Abandoned} lapsed with a crime left running.");

                Say($"any-player reach: {World.AnyPlayerReach.Hooked} of 8 patched; a teammate answered " +
                    $"for \"the player\" {World.AnyPlayerReach.Answered} time(s) (follow, wait, went away, left or entered a volume).");

                Say($"exp hook: {(World.ExperienceShare.HookFired ? "yes" : "no")}; " +
                    $"{World.ExperienceShare.Sent} kills shared, {World.ExperienceShare.Received} paid out " +
                    $"from others, {World.ExperienceShare.RefusedLocal} double payments refused.");

                if (World.ExperienceShare.Unshareable > 0 || World.ExperienceShare.Pending > 0)
                {
                    Say($"{World.ExperienceShare.Unshareable} kills were not in the session so could not be " +
                        $"shared, {World.ExperienceShare.Pending} payouts waiting for a player.");
                }

                // "INVINCIBLE" here answers "nothing can hurt me". The mod sets it only on puppets and
                // bound creatures, so here it means one of those paths caught the wrong body.
                if (PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self))
                {
                    Il2CppDrova.Health selfHealth = self.GetHealth();

                    if (selfHealth != null)
                    {
                        bool inSession = World.EnemyRegistry.TryGetSessionId(self, out EnemyNetId selfId);

                        Say($"local player: {selfHealth.CurHealth}/{selfHealth.MaxHealth}" +
                            (selfHealth.IsInvincible ? ", INVINCIBLE" : string.Empty) +
                            (selfHealth.HasDamageBlockerActive ? ", damage blocker active" : string.Empty) +
                            (inSession ? $", REGISTERED AS CREATURE {selfId} - this is the bug" : string.Empty) +
                            ".");
                    }
                }

                // A host claiming nothing in a camp, or a client bound to nothing, is the first fork for unshared health.
                if (CoopSession.State == CoopSessionState.Hosting)
                {
                    // The third number at zero while apart means the host is not building the teammate's creatures.
                    Say($"{World.EnemyRegistry.Claimed} creatures claimed, {World.EnemyPinning.Pinned} held " +
                        $"loaded, {World.EnemyPinning.HeldForRemotes} built for remote players.");
                }
                else if (World.EnemyRegistry.Bound > 0)
                {
                    Say($"{World.EnemyRegistry.Bound} creatures are the host's to decide here, " +
                        $"{World.RemoteEnemies.Count} of them driven, " +
                        $"{World.EnemyPinning.HeldForRemotes} built for the host.");
                }

                // The hooks that decide whether this client's save may call a creature dead. "- / -" means neither ran.
                if (CoopSession.State == CoopSessionState.Joined)
                {
                    Say($"world-state hooks fired: {World.HostWorldState.Fired}; " +
                        $"{World.HostWorldState.Noticed} local deaths queried against the host, " +
                        $"{World.EnemyRegistry.AliveByWord} answered alive by the host's word.");

                    // "save-health hook: no" means every rebuild comes back a corpse.
                    Say($"save-health hook: {(World.EnemyRebirth.HookFired ? "yes" : "no")}; " +
                        $"{World.EnemyRebirth.Rebuilt} creatures given a new body, " +
                        $"{World.EnemyRebirth.Overruled} kept alive as they streamed in, " +
                        $"{World.EnemyRebirth.GivenUp} left as corpses.");

                    if (World.RemoteEnemies.Desynced > 0)
                    {
                        Say($"{World.RemoteEnemies.Desynced} updates were refused for a creature this world " +
                            "has dead. A count that keeps climbing means a corpse the host is still driving.");
                    }
                }

                // Printed even at zero, since asked at zero means the patch is dead (inlined), not a quiet world.
                Say($"AI ticks: asked {World.EnemyPerception.Asked}, kept alive for a remote player " +
                    $"{World.EnemyPerception.Extended}.");

                Say($"behavior-tree range checks: asked {World.EnemyPerception.AskedCondition}, answered for " +
                    $"a remote player {World.EnemyPerception.ExtendedCondition}.");

                Say($"creatures kept built: asked {World.CameraReach.AskedActor}, kept " +
                    $"{World.CameraReach.KeptActor}, put away anyway and refused {World.CameraReach.RefusedRef}.");

                Say($"gvar changes: {World.GVarWatch.Reported} asserted to the session over " +
                    $"{World.GVarWatch.Known} variable(s), {World.RemoteGVar.Applied} stored from it " +
                    $"and {World.RemoteGVar.Replayed} consequence(s) run.");

                Say($"gvar health: {World.RemoteGVar.Refused} refused, " +
                    $"{World.GVarWatch.RefusedLocally} of this world's own writes refused by the game, " +
                    $"{World.RemoteGVar.Skipped} " +
                    $"order stamp(s) given up on, {World.RemoteGVar.Pending} waiting, " +
                    $"{World.RemoteGVar.Diverged} arrived onto a world that had already diverged, " +
                    $"{World.RemoteGVar.RegressionsRefused} finished quest(s) kept from reopening, " +
                    $"{World.GVarDerived.MirroredSkipped} mirrored count/toggle step(s) dropped. " +
                    $"Player-body effects held shut: {World.StoryEffectGate.Suppressed}" +
                    (World.StoryEffectGate.Installed ? string.Empty : " (GATE NOT INSTALLED)") +
                    (World.RemoteGVar.Quiet ? ". Consequences are switched OFF (coop_gvar_quiet)." : "."));

                Say($"cutscenes: {World.CutsceneWatch.Shared} shared from here, " +
                    $"{World.RemoteCutscene.Replayed} replayed for a teammate's story.");

                Say($"{World.PuppetTargets.Claimed} target checks treated a remote player as a player; " +
                    $"{World.PuppetTargets.Separated} gave one a target-list identity of its own.");

                Say($"{World.EnemyCognition.Restored} things were made noticeable near a remote player.");

                Say($"{World.EnemyThreat.Credited} reported blows were credited to the teammate who " +
                    "landed them.");

                Say($"{World.RemoteEnemyBlows.Discarded} blows from driven creatures were left to the " +
                    "host to report.");

                SayWork();

                // Piles named 0 after real mining means the spawn funnel is dead.
                foreach (string line in World.HaulShare.Describe())
                {
                    Say(line);
                }

                Say($"journal: {World.JournalShare.Shared} entr(ies) told to the session, " +
                    $"{World.JournalShare.Received} written from it, " +
                    $"{World.JournalShare.RefusedDuplicates} second cop(ies) refused.");

                // Gate answered 0 with moved above 0 means the postfix never reaches the AI's calls.
                Say($"npc placement: {World.NpcPlacement.Reconciled} moved to the host's world, " +
                    $"{World.NpcPlacement.Holding} gated now, {World.NpcPlacement.Released} let go on " +
                    $"unload. Routine gate answered {World.NpcPlacement.GateAnswered} time(s) " +
                    $"({World.NpcPlacement.QuestLegsLetThrough} quest leg(s) let through) this " +
                    $"session, unload hook {(World.NpcPlacement.UnloadHooked ? "yes" : "NO")}.");

                // Checked 0 means the patch applied and never fired.
                Say($"mine bed: hook {(World.MineDayLoop.Hooked ? "yes" : "NO")}, cursor checked " +
                    $"{World.MineDayLoop.Asked} time(s), moved on {World.MineDayLoop.Moved} time(s).");

                // A long frame expires every frame-counting timer in this mod.
                Say($"frames: {Core.LongFrames} frame(s) over 1.0s (longest {Core.LongestFrame:0.0}s), " +
                    $"{World.EnemyEngagements.StallsIgnored} of them not aged against a teammate's " +
                    "engagement.");

                Say($"scene moves: hook {(World.SceneMoveWatch.Hooked ? "yes" : "NO")}, " +
                    $"{World.SceneMoveWatch.Started} started, {World.SceneMoveWatch.Watching} running, " +
                    $"{World.SceneMoveWatch.Rescued} rescued from waiting forever.");

                // Announced 0 after a fight means the death watch swallowed its own event.
                Say($"enemy deaths: {World.EnemyDeathWatch.Reported} announced " +
                    $"({World.EnemyDeathWatch.FromTeammatesBlow} of them from a teammate's blow), " +
                    $"{World.EnemyDeathWatch.SuppressedAsEcho} suppressed as echoes.");

                SayClock();

                Say($"maps: {World.MapShare.Shared} unlock(s) told to the session, " +
                    $"{World.MapShare.Received} applied from it.");

                // Remembered 0 after a dialogue grant means the savegame write is dropping.
                Say($"dialogue rewards: {World.RewardShare.Shared} told to the session, " +
                    $"{World.RewardShare.Received} paid from it, {World.RewardShare.Remembered} " +
                    $"remembered in this savegame, {World.RewardShare.RememberedHits} repeat(s) it " +
                    $"caught, {World.RewardShare.RefusedWrites} write(s) refused.");

                Say($"dialogue items: {World.ItemGrantShare.Shared} told to the session, " +
                    $"{World.ItemGrantShare.HandedIn} relayed quest item(s) taken back after a teammate handed theirs in, " +
                    $"{World.ItemGrantShare.Received} granted from it, {World.ItemGrantShare.Remembered} " +
                    $"remembered in this savegame, {World.ItemGrantShare.RememberedHits} repeat(s) it " +
                    $"caught, {World.ItemGrantShare.RefusedWrites} write(s) refused.");

                Say($"story gates: brought along {World.GateFollow.Followed} time(s), " +
                    $"{World.GateFollow.StoryPortsReported} own story port(s) told to teammates; " +
                    $"story dialogues and waves held for a teammate's scene {World.StorySceneLease.Held} frame(s)" +
                    (World.StorySceneLease.Hooked ? "." : " (LEASE NOT PATCHED)."));

                Say($"engagements: {World.EngageWatch.Reported} transitions reported, " +
                    $"{World.EnemyEngagements.Held} dialogue hold(s) and {World.EnemyEngagements.Leases} brawl " +
                    $"lease(s) served here, {World.EnemyThreat.Pardoned} pardon(s).");

                Say($"joined brawls: invited into {World.TeamBrawls.Joined} of the host's, " +
                    $"{World.TeamBrawls.Assists} teammate blow(s) carried into this player's own.");

                // Printed even at zero. Asked at zero during brawls means the gate's patches are dead.
                Say($"brawl gate: asked {World.PuppetBrawlGate.Asked}, kept a remote player's body " +
                    $"out {World.PuppetBrawlGate.Refused} time(s).");

                Say($"teammate blows: {World.RemoteEnemyHealth.Refused} refused by this world's protections, " +
                    $"{World.RemoteEnemyHealth.BrawlSaves} brawl loser(s) knocked out instead of killed.");

                Say(World.WeatherSync.Describe());

                foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
                {
                    if (player.Key == CoopSession.LocalPlayerId)
                    {
                        Say($"  {player.Key}: {player.Value} (you)");
                        continue;
                    }

                    // Measured end to end, since the transport's ping only reaches the relay.
                    float? roundTrip = CoopSession.RoundTripMs(player.Key);
                    string latency = roundTrip.HasValue ? $"{roundTrip.Value:0} ms" : "measuring";

                    Say($"  {player.Key}: {player.Value} ({latency})");
                    SayWhereTheyStand(player.Key);
                }
            });
        }

        /// <summary>
        /// Reports how far away another player's body is and whether the world is loaded around it.
        /// Only meaningful once the players are far apart.
        /// </summary>
        private static void SayWhereTheyStand(PlayerId player)
        {
            if (!World.RemotePlayers.Bodies.TryGetValue(player, out World.RemotePlayer? body) || body == null) return;
            if (body.Body == null) return;

            string distance = PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor local)
                ? $"{Vector3.Distance(local.transform.position, body.Body.transform.position):0} units away"
                : "distance unknown";

            Say($"    {distance}, {body.DescribeStreaming()}");
        }

        /// <summary>
        /// The command's argument at <paramref name="index"/>, or null when it was not given.
        /// </summary>
        private static string? Arg(Il2CppReferenceArray<CommandArg> args, int index)
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
