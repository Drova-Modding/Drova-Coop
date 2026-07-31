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

#if DEBUG
            CheatMenuAccess.RegisterCheat("coop_guids", OnGuids, 0, 1,
                "Report which creatures can still be found by the guid they report. Optional range in units.",
                "coop_guids [range]");
#endif

            MelonLogger.Msg("[Coop] Console commands ready: coop_host, coop_join, coop_leave, coop_status."
#if DEBUG
                + " Diagnostics: coop_guids."
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

                Say($"exp hook: {(World.ExperienceShare.HookFired ? "yes" : "no")}; " +
                    $"{World.ExperienceShare.Sent} kills shared, {World.ExperienceShare.Received} paid out " +
                    $"from others, {World.ExperienceShare.RefusedLocal} double payments refused.");

                if (World.ExperienceShare.Unshareable > 0 || World.ExperienceShare.Pending > 0)
                {
                    Say($"{World.ExperienceShare.Unshareable} kills were not in the session so could not be " +
                        $"shared, {World.ExperienceShare.Pending} payouts waiting for a player.");
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
                        $"{World.HostWorldState.Noticed} local deaths queried against the host.");

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

                Say($"bodies kept hittable: asked {World.CameraReach.AskedReceive}, kept " +
                    $"{World.CameraReach.KeptReceive}; swings kept live: asked " +
                    $"{World.CameraReach.AskedTrigger}, kept {World.CameraReach.KeptTrigger}.");

                Say($"{World.PuppetTargets.Claimed} target checks treated a remote player as a player; " +
                    $"{World.PuppetTargets.Separated} gave one a target-list identity of its own.");

                Say($"{World.EnemyCognition.Restored} things were made noticeable near a remote player.");

                Say($"{World.EnemyThreat.Credited} reported blows were credited to the teammate who " +
                    "landed them.");

                Say($"{World.RemoteEnemyBlows.Discarded} blows from driven creatures were left to the " +
                    "host to report.");

                if (World.RemoteEnemyDeath.Refused > 0)
                {
                    Say($"{World.RemoteEnemyDeath.Refused} reported deaths were refused: this world still " +
                        "protects those actors from dying.");
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
