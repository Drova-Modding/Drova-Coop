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

            CheatMenuAccess.RegisterCheat("coop_revive", OnRevive, 0, 1,
                "Host only: revive every dead creature near this player, in the world and in the save. " +
                "Optional range in units, default 300.",
                "coop_revive [range]");

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

            MelonLogger.Msg("[Coop] Console commands ready: coop_host, coop_join, coop_leave, coop_status, " +
                "coop_tp, coop_revive, coop_resync, coop_backups, coop_restore. Diagnostics: coop_gvars, " +
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
            // The typed argument wins over the configured code, because somebody typing a code into a
            // console has just been given one and does not expect to edit a preferences file first. That
            // preference is what CoopActions falls back to when nothing was typed.
            Run(() => Say(CoopActions.Join(args != null && args.Length > 0 ? args[0].String : null)));
        }

        private static void OnLeave(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() => Say(CoopActions.Leave()));
        }

#if DEBUG
        /// <summary>
        /// Reports which creatures can still be found by the guid they report. See
        /// <see cref="Diagnostics.GuidReport"/> for what that answers and why nothing else asks it.
        /// </summary>
        private static void OnGuids(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                float range = 0f;

                if (args != null && args.Length > 0)
                {
                    // Parsed here rather than through CommandArg.Float, which logs its own complaint to the
                    // console when the text is not a number and returns zero either way.
                    float.TryParse(args[0].String, out range);
                }

                Diagnostics.GuidReport.Run(range, Say);
            });
        }
#endif

        /// <summary>
        /// Revives every dead creature lying near the host's player, in the world and in the save.
        ///
        /// The repair for a save poisoned by pre-fix sessions, where a joining client's save corpses were
        /// written into the host's world as deaths. Host only, because the host's save is the world every
        /// other machine follows. It cannot tell a camp from a story corpse, so the range is small and the
        /// player chooses where to stand.
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

                float range = 300f;

                if (args != null && args.Length > 0)
                {
                    float.TryParse(args[0].String, out range);
                }

                if (range <= 0f)
                {
                    range = 300f;
                }

                if (!PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self) || self == null)
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
                System.Collections.Generic.List<string> revived = new();

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

                // The push. Everybody connected rebuilds the same corpses now, instead of finding out
                // whenever they next join.
                CoopSession.AnnounceEnemyRevive(revived);

                Say(revived.Count == 0
                    ? $"Nothing dead within {range:0} units."
                    : $"{revived.Count} creature(s) within {range:0} units called alive again, here and on " +
                      "every connected machine. The next autosave makes it permanent. This cannot tell a " +
                      "camp from a story corpse, so run it standing in the camp.");
            });
        }

        /// <summary>
        /// Teleports this player to a teammate's body.
        ///
        /// Matching is forgiving - an id digit or any part of the name - because the caller is
        /// standing lost somewhere and typing into a console. Refuses on ambiguity rather than
        /// guessing, since the wrong guess is a long walk back.
        /// </summary>
        private static void OnTeleport(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
                {
                    Say("Not in a session.");
                    return;
                }

                string? wanted = args != null && args.Length > 0 ? args[0].String : null;
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
                    string names = string.Empty;

                    foreach (KeyValuePair<PlayerId, string> match in matches)
                    {
                        names += (names.Length > 0 ? ", " : string.Empty) + $"{match.Key}: {match.Value}";
                    }

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

                if (!PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self) || self == null)
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
        ///
        /// This replaces <c>coop_repair_journal</c> and <c>coop_repair_gates</c>, which are gone. Both
        /// worked by resetting variables to their authored initials, on the theory that the next
        /// catch-up would then replay them as real transitions - and both did considerably more than
        /// that theory allowed. The quest-state reset walked every synchronized quest in the save, so
        /// a real playthrough lost its finished quests: they dropped back to available while their
        /// journal topics stayed in the completed category, and the graph node that had set them was
        /// already retired and could never set them again. Asking for the world back is the same
        /// intent without the demolition.
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

                bool deep = args != null && args.Length > 0
                    && string.Equals(args[0].String, "deep", StringComparison.OrdinalIgnoreCase);

                CoopSession.RequestResync(Messages.GVarResyncRequestMessage.ReasonManual, deep);

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
        /// Lists the global variables that have moved from their initial values. See
        /// <see cref="World.GVarSnapshot.Report"/> for what that answers.
        /// </summary>
        private static void OnGVarReport(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                string filter = args != null && args.Length > 0 ? args[0].String : string.Empty;

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
                string filter = args != null && args.Length > 0 ? args[0].String : string.Empty;

                World.GVarWatch.Trace = filter;

                Say(string.IsNullOrEmpty(filter)
                    ? "No longer tracing variable assertions."
                    : $"Tracing every variable this machine asserts whose name contains '{filter}'.");
            });
        }

        /// <summary>
        /// Reports the shared-mining subsystem, and switches replaying off when it misbehaves.
        ///
        /// Runs outside a session too, which is the point: the hook counts answer "did the game
        /// inline my funnel away" without needing a second machine.
        /// </summary>
        private static void OnClock(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                string arg = args != null && args.Length > 0 ? args[0].String : string.Empty;

                if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase))
                {
                    World.ClockGuard.Enabled = string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase);

                    Say(World.ClockGuard.Enabled
                        ? "Only the host's clock turns the world over again."
                        : "This machine may advance its own day again. On a client that fires every " +
                          "routine port and every day-forward flag in this world alone, sends those " +
                          "flags to the host, and is then pulled back with no way to undo any of it.");

                    return;
                }

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
            });
        }

        private static void OnHaul(Il2CppReferenceArray<CommandArg> args)
        {
            Run(() =>
            {
                string arg = args != null && args.Length > 0 ? args[0].String : string.Empty;

                if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase))
                {
                    World.HaulShare.Enabled = string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase);

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
                string arg = args != null && args.Length > 0 ? args[0].String : string.Empty;

                if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase))
                {
                    World.ResourceWork.Enabled = string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase);

                    Say(World.ResourceWork.Enabled
                        ? "Teammates' swings are replayed here again."
                        : "Teammates' swings will no longer be replayed here. Mining goes back to " +
                          "being per-machine for the rest of this session.");

                    return;
                }

                Say($"hooks {World.ResourceWork.HooksInstalled} of 5; {World.ResourceWork.MinigameHits} " +
                    $"swing(s) seen here, {World.ResourceWork.RegisterHits} spot registration(s).");

                if (World.ResourceWork.HooksInstalled < 5 || World.ResourceWork.MinigameHits == 0)
                {
                    Say("Mine one node and run this again. If the swing count stays at zero the game " +
                        "inlined the funnel away and nothing else here means anything.");
                }

                Say($"{World.ResourceWork.Registered} spot(s) addressable, " +
                    $"{World.ResourceWork.Pending} held for chunks that are not loaded.");

                Say($"{World.ResourceWork.Worked} told, {World.ResourceWork.Applied} applied, " +
                    $"{World.ResourceWork.AlreadyThere} already there, {World.ResourceWork.Refused} refused.");
            });
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
                bool quiet = args == null || args.Length == 0
                    || !string.Equals(args[0].String, "off", StringComparison.OrdinalIgnoreCase);

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
                Say($"build {Versioning.VersionPolicy.Format(VersionCheck.RunningVersion)}; version policy: {VersionCheck.Summary}");

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

                // The two halves of shared death, and they answer different questions. The first says how
                // much of the world this session has agreed is gone; the second says how much of that
                // agreement this machine has not been able to act on, because the bodies have not streamed
                // in yet. A pending count that never falls is the symptom of a guid that means nothing
                // here - a creature spawned at runtime rather than authored into the scene.
                if (CoopSession.KnownDeadEnemies > 0 || World.RemoteEnemyDeath.Pending > 0)
                {
                    Say($"{CoopSession.KnownDeadEnemies} creatures known dead, " +
                        $"{World.RemoteEnemyDeath.Pending} waiting for a body to apply it to.");
                }

                // Both are expected to be non-zero and neither is a fault, which is why they say what they
                // mean rather than being counted as errors. The first is how much of the world is spawned
                // rather than authored; the second is how far apart the two players' stories have drifted.
                if (World.EnemyDeathWatch.Unnameable > 0)
                {
                    Say($"{World.EnemyDeathWatch.Unnameable} local deaths could not be shared: spawned at " +
                        "runtime, so no other machine can name them.");
                }

                // Told apart from the rest because these have a way out. A count climbing here while nothing
                // is being announced as a spawn means the mod that made those creatures is not going through
                // the API's spawn path, so they exist for one player only.
                if (World.EnemyDeathWatch.SpawnedElsewhere > 0 || World.SpawnedEnemies.Count > 0
                    || World.SpawnedEnemies.Pending > 0)
                {
                    Say($"{World.SpawnedEnemies.Count} spawned creatures shared, " +
                        $"{World.SpawnedEnemies.Pending} noticed and not shared yet, " +
                        $"{World.SpawnedEnemies.Unbuildable} announced but unbuildable here, " +
                        $"{World.EnemyDeathWatch.SpawnedElsewhere} deaths skipped as spawned-elsewhere.");
                }

                // A summon exists on both machines whether or not this works - the cast is replicated - so
                // the failure here is two independent creatures rather than a missing one, which reads as a
                // spell that fights differently on each screen.
                if (World.SummonWatch.Shared > 0 || World.SummonWatch.Paired > 0 || World.SummonWatch.Unnamed > 0)
                {
                    Say($"conjure hook: {(World.SummonWatch.HookFired ? "yes" : "no")}; " +
                        $"{World.SummonWatch.Shared} summons claimed, {World.SummonWatch.Paired} paired here, " +
                        $"{World.SummonWatch.Unnamed} left local (a creature's own summon, not a player's).");
                }

                // "exp hook: no" means nothing is shared and the two players level apart, which looks like
                // nothing at all for the first hour.
                if (World.DamageWatch.KeptAlive > 0)
                {
                    Say($"{World.DamageWatch.KeptAlive} times a remote player's body was held back from " +
                        "dying here. Their own game decides that; a corpse here could never stand up.");
                }

                // Only worth a line when the rule is on. "hook: no" with a rule that is not a hundred
                // means creatures are fighting at their normal health whatever the slider says.
                if (CoopSession.EnemyHealthPercent != CoopProtocol.DefaultEnemyHealthPercent || World.EnemyScaling.Scaled > 0)
                {
                    Say($"enemy health {CoopSession.EnemyHealthPercent}%; scaling hook: " +
                        $"{(World.EnemyScaling.Hooked ? "applied" : "NOT APPLIED")}, " +
                        $"{(World.EnemyScaling.HookFired ? "fired" : "never fired")}; " +
                        $"{World.EnemyScaling.Scaled} blows scaled here.");
                }

                Say($"exp hook: {(World.ExperienceShare.HookFired ? "yes" : "no")}; " +
                    $"{World.ExperienceShare.Sent} kills shared, {World.ExperienceShare.Received} paid out " +
                    $"from others, {World.ExperienceShare.RefusedLocal} double payments refused.");

                if (World.ExperienceShare.Unshareable > 0 || World.ExperienceShare.Pending > 0)
                {
                    Say($"{World.ExperienceShare.Unshareable} kills were not in the session so could not be " +
                        $"shared, {World.ExperienceShare.Pending} payouts waiting for a player.");
                }

                // The local player's own health state, because everything else here is about other
                // bodies. "INVINCIBLE" on this line is the answer to "nothing can hurt me": the game
                // computes every blow, fires every event - feedback, poise, blood - and discards the
                // write, which from the outside is indistinguishable from every attacker being broken.
                // The mod only ever sets that flag on puppets and bound creatures, so seeing it here
                // means one of those paths caught the wrong body - which is what the last part checks.
                if (PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self) && self != null)
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

                // Which end this machine is doing the work at. A host that claims nothing while standing
                // in a camp, or a client bound to nothing while its friend is fighting, is the first fork
                // in diagnosing enemy health that is not shared.
                if (CoopSession.State == CoopSessionState.Hosting)
                {
                    // The third number is the one that says whether the host can reach a friend who has
                    // walked off. Zero while apart means the host is not building the creatures they are
                    // standing among, so there is nothing there for it to claim and nothing crosses.
                    Say($"{World.EnemyRegistry.Claimed} creatures claimed, {World.EnemyPinning.Pinned} held " +
                        $"loaded, {World.EnemyPinning.HeldForRemotes} built for remote players.");
                }
                else if (World.EnemyRegistry.Bound > 0)
                {
                    // The last number is this end of the same reach: a creature the host drives beside
                    // itself has to exist here before anything it sends can be applied to it.
                    Say($"{World.EnemyRegistry.Bound} creatures are the host's to decide here, " +
                        $"{World.RemoteEnemies.Count} of them driven, " +
                        $"{World.EnemyPinning.HeldForRemotes} built for the host.");
                }

                // The two hooks that decide whether this client's own save gets to say a creature is dead.
                // "- / -" means neither ever ran, which is the difference between "the fix is wrong" and
                // "the fix was never asked" - and with both dashes, a client will keep finding its own
                // kills however correct everything downstream is.
                if (CoopSession.State == CoopSessionState.Joined)
                {
                    Say($"world-state hooks fired: {World.HostWorldState.Fired}; " +
                        $"{World.HostWorldState.Noticed} local deaths queried against the host, " +
                        $"{World.EnemyRegistry.AliveByWord} answered alive by the host's word.");

                    // The three numbers that say whether a camp this player cleared in their own game is a
                    // fight again. "save-health hook: no" means every rebuild is doomed before it starts -
                    // the body gets destroyed and comes back a corpse - which is the one failure here that
                    // looks identical to the feature simply not being needed.
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

                // Printed even at zero, and as asked/kept pairs. A rule this mod failed to widen and a
                // patch that never ran read identically from a single number, and this build folds small
                // methods into their callers - so asked at zero means the patch is dead, not that the
                // world is quiet.
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
                    $"{World.RemoteGVar.Diverged} arrived onto a world that had already diverged. " +
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

                // Read this block top-down: a hook count below 5, or a swing count of zero after a
                // real swing, means the game inlined the funnel away and nothing below it means
                // anything. That distinction has cost this project a whole playthrough before.
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

                // Read this block top-down as well: piles named 0 after real mining means the spawn
                // funnel is dead and nothing below it means anything.
                foreach (string line in World.HaulShare.Describe())
                {
                    Say(line);
                }

                Say($"journal: {World.JournalShare.Shared} entr(ies) told to the session, " +
                    $"{World.JournalShare.Received} written from it, " +
                    $"{World.JournalShare.RefusedDuplicates} second cop(ies) refused.");

                // Gate answered 0 with moved above 0 means the postfix never reaches the AI's own
                // interface-dispatched calls, and whatever the gate is thought to be doing, it is not.
                Say($"npc placement: {World.NpcPlacement.Reconciled} moved to the host's world, " +
                    $"{World.NpcPlacement.Holding} gated now, {World.NpcPlacement.Released} let go on " +
                    $"unload. Routine gate answered {World.NpcPlacement.GateAnswered} time(s) this " +
                    $"session, unload hook {(World.NpcPlacement.UnloadHooked ? "yes" : "NO")}.");

                // Checked 0 is the failure that looks like success: the patch applied and never fired.
                Say($"mine bed: hook {(World.MineDayLoop.Hooked ? "yes" : "NO")}, cursor checked " +
                    $"{World.MineDayLoop.Asked} time(s), moved on {World.MineDayLoop.Moved} time(s).");

                // The shared alarm for every timer in this mod that counts frames. A long frame tells
                // each of them that nothing happened, because nothing was observed.
                Say($"frames: {Core.LongFrames} frame(s) over 1.0s (longest {Core.LongestFrame:0.0}s), " +
                    $"{World.EnemyEngagements.StallsIgnored} of them not aged against a teammate's " +
                    "engagement.");

                Say($"scene moves: hook {(World.SceneMoveWatch.Hooked ? "yes" : "NO")}, " +
                    $"{World.SceneMoveWatch.Started} started, {World.SceneMoveWatch.Watching} running, " +
                    $"{World.SceneMoveWatch.Rescued} rescued from waiting forever.");

                // Announced 0 after a fight is the regression this counter exists to catch: the death
                // watch swallowing the very event it exists to carry, silently, as it once did.
                Say($"enemy deaths: {World.EnemyDeathWatch.Reported} announced " +
                    $"({World.EnemyDeathWatch.FromTeammatesBlow} of them from a teammate's blow), " +
                    $"{World.EnemyDeathWatch.SuppressedAsEcho} suppressed as echoes.");

                Say($"time: {World.DaytimeSync.FollowedSleeps} host sleep(s) followed, " +
                    $"{World.DaytimeSync.Rewinds} backward correction(s) over an hour " +
                    $"(largest {World.DaytimeSync.LargestRewindHours:0.#} h).");

                Say($"clock: guard {(World.ClockGuard.Hooked ? "yes" : "NO")}, " +
                    $"{World.ClockGuard.Refused} local day advance(s) refused " +
                    $"(largest {World.ClockGuard.LargestRefusedHours:0.#} h), " +
                    $"{World.ClockGuard.Asked} of them passed to the host as a sleep; " +
                    $"slept for a teammate {World.DaytimeSync.Granted} time(s), " +
                    $"{World.DaytimeSync.RefusedAsks} ask(s) turned down." +
                    (World.ClockGuard.Enabled ? "" : " GUARD IS OFF (coop_clock on)."));

                Say($"maps: {World.MapShare.Shared} unlock(s) told to the session, " +
                    $"{World.MapShare.Received} applied from it.");

                // Remembered 0 after a session in which any dialogue granted anything means the
                // savegame write is silently dropping and a reload will pay the one-off again.
                Say($"dialogue rewards: {World.RewardShare.Shared} told to the session, " +
                    $"{World.RewardShare.Received} paid from it, {World.RewardShare.Remembered} " +
                    $"remembered in this savegame, {World.RewardShare.RememberedHits} repeat(s) it " +
                    $"caught, {World.RewardShare.RefusedWrites} write(s) refused.");

                Say($"dialogue items: {World.ItemGrantShare.Shared} told to the session, " +
                    $"{World.ItemGrantShare.Received} granted from it, {World.ItemGrantShare.Remembered} " +
                    $"remembered in this savegame, {World.ItemGrantShare.RememberedHits} repeat(s) it " +
                    $"caught, {World.ItemGrantShare.RefusedWrites} write(s) refused.");

                Say($"story gates: brought along {World.GateFollow.Followed} time(s).");

                Say($"engagements: {World.EngageWatch.Reported} transitions reported, " +
                    $"{World.EnemyEngagements.Held} dialogue hold(s) and {World.EnemyEngagements.Leases} brawl " +
                    $"lease(s) served here, {World.EnemyThreat.Pardoned} pardon(s).");

                Say($"joined brawls: invited into {World.TeamBrawls.Joined} of the host's, " +
                    $"{World.TeamBrawls.Assists} teammate blow(s) carried into this player's own.");

                // Printed even at zero. Asked at zero while brawls are happening means the gate's
                // patches are dead, not that no puppet ever wandered into one.
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
