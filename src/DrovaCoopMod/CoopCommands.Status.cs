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
using UnityEngine;

namespace DrovaCoopMod
{
    // coop_status: everything a bug report needs, in one command.
    internal static partial class CoopCommands
    {
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
                else if (CoopSession.DirectAddress != null)
                {
                    Say($"direct to {CoopSession.DirectAddress}:{CoopSession.DirectPort} (NOT encrypted)");
                }
                else if (CoopSession.DirectPort != 0)
                {
                    Say($"hosting directly on UDP port {CoopSession.DirectPort} (NOT encrypted): {CoopActions.AddressLine(CoopSession.DirectPort)}");
                }

                Say("patches off: " + HookSwitches.Disabled);

                // Some fire only in rare scenes. One that stays silent through a scene it belongs to is a dead hook.
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
                if (RemoteEnemyDeath.KnownDeadEnemies > 0 || RemoteEnemyDeath.Pending > 0)
                {
                    Say($"{RemoteEnemyDeath.KnownDeadEnemies} creatures known dead, " +
                        $"{RemoteEnemyDeath.Pending} waiting for a body to apply it to, " +
                        $"{RemoteEnemyDeath.Credited} unloaded kill(s) counted by this world's quest observers.");
                }

                // Both are normally non-zero: runtime spawns, and story drift between the players.
                if (EnemyDeathWatch.Unnameable > 0)
                {
                    Say($"{EnemyDeathWatch.Unnameable} local deaths could not be shared: spawned at " +
                        "runtime, so no other machine can name them.");
                }

                // Climbing with no spawns announced means a mod bypasses the API's spawn path.
                if (EnemyDeathWatch.SpawnedElsewhere > 0 || SpawnedEnemies.Count > 0
                    || SpawnedEnemies.Pending > 0)
                {
                    Say($"{SpawnedEnemies.Count} spawned creatures shared, " +
                        $"{SpawnedEnemies.Pending} noticed and not shared yet, " +
                        $"{SpawnedEnemies.Unbuildable} announced but unbuildable here, " +
                        $"{EnemyDeathWatch.SpawnedElsewhere} deaths skipped as spawned-elsewhere.");
                }

                // Failure here means two independent summons rather than a missing one.
                if (SummonWatch.Shared > 0 || SummonWatch.Paired > 0 || SummonWatch.Unnamed > 0)
                {
                    Say($"conjure hook: {(SummonWatch.HookFired ? "yes" : "no")}; " +
                        $"{SummonWatch.Shared} summons claimed, {SummonWatch.Paired} paired here, " +
                        $"{SummonWatch.Unnamed} left local (a creature's own summon, not a player's).");
                }

                if (DamageWatch.KeptAlive > 0)
                {
                    Say($"{DamageWatch.KeptAlive} times a remote player's body was held back from " +
                        "dying here. Their own game decides that; a corpse here could never stand up.");
                }

                if (CoopSession.ArenaMode) Say("arena mode: each player keeps their own world; only fights between players are shared.");

                // "hook: no" with the rule on means creatures fight at normal health.
                if (CoopSession.EnemyHealthPercent != CoopProtocol.DefaultEnemyHealthPercent
                    || CoopSession.EnemyDamagePercent != CoopProtocol.DefaultEnemyDamagePercent || EnemyScaling.Scaled > 0)
                {
                    Say($"enemy health {CoopSession.EnemyHealthPercent}%, enemy damage {CoopSession.EnemyDamagePercent}%; scaling hook: " +
                        $"{(EnemyScaling.Hooked ? "applied" : "NOT APPLIED")}, " +
                        $"{(EnemyScaling.HookFired ? "fired" : "never fired")}; " +
                        $"{EnemyScaling.Scaled} blows scaled here.");
                }

                if (CoopSession.LootMode == CoopProtocol.LootModeShared || LootShare.Known > 0)
                {
                    Say($"loot {(CoopSession.LootMode == CoopProtocol.LootModeShared ? "shared" : "per player")}; " +
                        $"hooks {LootShare.HooksInstalled} of 6; {LootShare.Addressable} loaded here under a " +
                        $"shared name; {LootShare.Proposed} chest(s) offered, {LootShare.ProposalsRefused} refused here; " +
                        $"{LootShare.Sent} take(s) sent, {LootShare.Applied} applied, " +
                        $"{LootShare.Held} waiting for their object; {LootShare.Known} on the ledger; " +
                        $"{LootShare.BodiesNamed} bodies named, {LootShare.BodiesWaiting} waiting for a creature.");

                    Say($"caches: hooks {CacheShare.HooksInstalled} of 3; " +
                        $"{CacheShare.Addressable} loaded here under a shared name, " +
                        $"{CacheShare.Spilled} spilled pickup(s) kept out of the ledger; " +
                        $"{CacheShare.Sent} sent, {CacheShare.Applied} applied, " +
                        $"{CacheShare.Refused} offers refused here.");
                }

                if (Arena.Active)
                {
                    Say($"arena: marked at {Arena.Center.x:0.#},{Arena.Center.y:0.#}, " +
                        $"{Arena.Radius:0.#} units; {(Arena.Fighting ? "fight on" : "no fight")}; round {Arena.Round}" +
                        $"{(Arena.RoundLimit == 0 ? string.Empty : $" of {Arena.RoundLimit}")}, " +
                        $"{Arena.Rounds} called; you are {(Arena.HoldsLocal() ? "inside" : "outside")}, team {Arena.TeamOf(CoopSession.LocalPlayerId)}; {Arena.DescribeScore()}.");
                }
                else if (Arena.TeamOf(CoopSession.LocalPlayerId) is byte team and not 0)
                {
                    Say($"arena: none marked; you fight for team {team}.");
                }

                Say($"block verdicts: {(BlockVerdicts.Hooked ? "puppet guards refused" : "puppet guards DECIDE LOCALLY")}; " +
                    $"{BlockVerdicts.Suppressed} local decision(s) refused, {BlockVerdicts.Sent} verdict(s) sent, " +
                    $"{BlockVerdicts.Applied} guard break(s) played out.");

                Say($"world memory: {(LedgerStore.Loaded ? "read" : "not read yet")}; " +
                    $"{LedgerStore.PickupsRead} pickup(s) and {LedgerStore.ObjectsRead} object(s) " +
                    $"remembered from earlier sessions, {LedgerStore.Written} written at the last save.");

                // Save marks and world memory ride the backups' save prefix, so one switch turns off all three.
                if (!SaveBackups.Hooked)
                {
                    Say("the save hook is not applied, so slots are not marked and this world remembers " +
                        "nothing across a restart either. Check the 'backups' hook switch.");
                }

                Say($"coop saves: {CoopSaveMarks.Marked} slot(s) marked this run, " +
                    $"{CoopSaveMarks.Warnings} played alone after a session; marks under UserData/{CoopSaveMarks.FolderName}.");

                Say($"game mode: {Checkpoint.Mode}; party checkpoint {(Checkpoint.Committed == 0 ? "none yet" : Checkpoint.Committed.ToString())}" +
                    (Checkpoint.AllowsGoingBack ? "." : " (going back by choice is off in this mode)."));
                Say($"world objects: hooks {WorldObjects.HooksInstalled} of {WorldObjects.HookCount}; " +
                    $"{WorldObjects.Addressable} loaded here under a shared name; " +
                    $"{WorldObjects.Sent} change(s) sent, {WorldObjects.Applied} applied, " +
                    $"{WorldObjects.Held} waiting for their object, {WorldObjects.Abandoned} given up on; {WorldObjects.Known} on the ledger" +
                    $"{(CoopSession.State == CoopSessionState.Joined && !WorldObjects.SnapshotComplete ? "; join snapshot still arriving" : string.Empty)}.");

                Say($"guard reach: {(GuardReach.Hooked ? "patched" : "NOT patched")}; " +
                    $"{GuardReach.Entered} crime volume(s) entered by a teammate, {GuardReach.Left} left, " +
                    $"{GuardReach.Holding} held now, {GuardReach.Abandoned} lapsed with a crime left running.");

                Say($"any-player reach: {AnyPlayerReach.Hooked} of 8 patched; a teammate answered " +
                    $"for \"the player\" {AnyPlayerReach.Answered} time(s) (follow, wait, went away, left or entered a volume).");

                // "exp hook: no" means the players level apart.
                Say($"exp hook: {(ExperienceShare.HookFired ? "yes" : "no")}; " +
                    $"{ExperienceShare.Sent} kills shared, {ExperienceShare.Received} paid out " +
                    $"from others, {ExperienceShare.RefusedLocal} double payments refused.");

                if (ExperienceShare.Unshareable > 0 || ExperienceShare.Pending > 0)
                {
                    Say($"{ExperienceShare.Unshareable} kills were not in the session so could not be " +
                        $"shared, {ExperienceShare.Pending} payouts waiting for a player.");
                }

                // The mod makes only puppets and bound creatures invincible, so INVINCIBLE here means one of those paths caught the wrong body.
                if (PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self))
                {
                    Il2CppDrova.Health selfHealth = self.GetHealth();

                    if (selfHealth != null)
                    {
                        bool inSession = EnemyRegistry.TryGetSessionId(self, out EnemyNetId selfId);

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
                    Say($"{EnemyRegistry.Claimed} creatures claimed, {EnemyPinning.Pinned} held " +
                        $"loaded, {EnemyPinning.HeldForRemotes} built for remote players.");
                }
                else if (EnemyRegistry.Bound > 0)
                {
                    Say($"{EnemyRegistry.Bound} creatures are the host's to decide here, " +
                        $"{RemoteEnemies.Count} of them driven, " +
                        $"{EnemyPinning.HeldForRemotes} built for the host.");
                }

                // The hooks that decide whether this client's save may call a creature dead. "- / -" means neither ran.
                if (CoopSession.State == CoopSessionState.Joined)
                {
                    Say($"world-state hooks fired: {HostWorldState.Fired}; " +
                        $"{HostWorldState.Noticed} local deaths queried against the host, " +
                        $"{EnemyRegistry.AliveByWord} answered alive by the host's word.");

                    // "save-health hook: no" means every rebuild comes back a corpse.
                    Say($"save-health hook: {(EnemyRebirth.HookFired ? "yes" : "no")}; " +
                        $"{EnemyRebirth.Rebuilt} creatures given a new body, " +
                        $"{EnemyRebirth.Overruled} kept alive as they streamed in, " +
                        $"{EnemyRebirth.GivenUp} left as corpses.");

                    if (RemoteEnemies.Desynced > 0)
                    {
                        Say($"{RemoteEnemies.Desynced} updates were refused for a creature this world " +
                            "has dead. A count that keeps climbing means a corpse the host is still driving.");
                    }
                }

                // Printed even at zero, since asked at zero means the patch is dead (inlined), not a quiet world.
                Say($"AI ticks: asked {EnemyPerception.Asked}, kept alive for a remote player " +
                    $"{EnemyPerception.Extended}.");

                Say($"behavior-tree range checks: asked {EnemyPerception.AskedCondition}, answered for " +
                    $"a remote player {EnemyPerception.ExtendedCondition}.");

                Say($"creatures kept built: asked {CameraReach.AskedActor}, kept " +
                    $"{CameraReach.KeptActor}, put away anyway and refused {CameraReach.RefusedRef}.");

                Say($"gvar changes: {GVarWatch.Reported} asserted to the session over " +
                    $"{GVarWatch.Known} variable(s), {RemoteGVar.Applied} stored from it " +
                    $"and {RemoteGVar.Replayed} consequence(s) run.");

                Say($"gvar health: {RemoteGVar.Refused} refused, " +
                    $"{GVarWatch.RefusedLocally} of this world's own writes refused by the game, " +
                    $"{RemoteGVar.Skipped} " +
                    $"order stamp(s) given up on, {RemoteGVar.Pending} waiting, " +
                    $"{RemoteGVar.Diverged} arrived onto a world that had already diverged, " +
                    $"{RemoteGVar.RegressionsRefused} finished quest(s) kept from reopening, " +
                    $"{GVarDerived.MirroredSkipped} mirrored count/toggle step(s) dropped. " +
                    $"Player-body effects held shut: {StoryEffectGate.Suppressed}" +
                    (StoryEffectGate.Installed ? string.Empty : " (GATE NOT INSTALLED)") +
                    (RemoteGVar.Quiet ? ". Consequences are switched OFF (coop_gvar_quiet)." : "."));

                Say($"cutscenes: {CutsceneWatch.Shared} shared from here, " +
                    $"{RemoteCutscene.Replayed} replayed for a teammate's story.");

                Say($"{PuppetTargets.Claimed} target checks treated a remote player as a player; " +
                    $"{PuppetTargets.Separated} gave one a target-list identity of its own.");

                Say($"{EnemyCognition.Restored} things were made noticeable near a remote player.");

                Say($"{EnemyThreat.Credited} reported blows were credited to the teammate who " +
                    "landed them.");

                Say($"{RemoteEnemyBlows.Discarded} blows from driven creatures were left to the " +
                    "host to report.");

                SayWork();

                // Piles named 0 after real mining means the spawn funnel is dead.
                foreach (string line in HaulShare.Describe())
                {
                    Say(line);
                }

                Say($"journal: {JournalShare.Shared} entr(ies) told to the session, " +
                    $"{JournalShare.Received} written from it, " +
                    $"{JournalShare.RefusedDuplicates} second cop(ies) refused.");

                // Gate answered 0 with moved above 0 means the postfix never reaches the AI's calls.
                Say($"npc placement: {NpcPlacement.Reconciled} moved to the host's world, " +
                    $"{NpcPlacement.Holding} gated now, {NpcPlacement.Released} let go on " +
                    $"unload. Routine gate answered {NpcPlacement.GateAnswered} time(s) " +
                    $"({NpcPlacement.QuestLegsLetThrough} quest leg(s) let through) this " +
                    $"session, unload hook {(NpcPlacement.UnloadHooked ? "yes" : "NO")}.");

                // Checked 0 means the patch applied and never fired.
                Say($"mine bed: hook {(MineDayLoop.Hooked ? "yes" : "NO")}, cursor checked " +
                    $"{MineDayLoop.Asked} time(s), moved on {MineDayLoop.Moved} time(s).");

                // A long frame expires every frame-counting timer in this mod.
                Say($"frames: {Core.LongFrames} frame(s) over 1.0s (longest {Core.LongestFrame:0.0}s), " +
                    $"{EnemyEngagements.StallsIgnored} of them not aged against a teammate's " +
                    "engagement.");

                Say($"scene moves: hook {(SceneMoveWatch.Hooked ? "yes" : "NO")}, " +
                    $"{SceneMoveWatch.Started} started, {SceneMoveWatch.Watching} running, " +
                    $"{SceneMoveWatch.Rescued} rescued from waiting forever.");

                // Announced 0 after a fight means the death watch swallowed its own event.
                Say($"enemy deaths: {EnemyDeathWatch.Reported} announced " +
                    $"({EnemyDeathWatch.FromTeammatesBlow} of them from a teammate's blow), " +
                    $"{EnemyDeathWatch.SuppressedAsEcho} suppressed as echoes.");

                SayClock();

                Say($"maps: {MapShare.Shared} unlock(s) told to the session, " +
                    $"{MapShare.Received} applied from it.");

                // Remembered 0 after a dialogue grant means the savegame write is dropping.
                Say($"dialogue rewards: {RewardShare.Shared} told to the session, " +
                    $"{RewardShare.Received} paid from it, {RewardShare.Remembered} " +
                    $"remembered in this savegame, {RewardShare.RememberedHits} repeat(s) it " +
                    $"caught, {RewardShare.RefusedWrites} write(s) refused.");

                Say($"dialogue items: {ItemGrantShare.Shared} told to the session, " +
                    $"{ItemGrantShare.HandedIn} relayed quest item(s) taken back after a teammate handed theirs in, " +
                    $"{ItemGrantShare.Received} granted from it, {ItemGrantShare.Remembered} " +
                    $"remembered in this savegame, {ItemGrantShare.RememberedHits} repeat(s) it " +
                    $"caught, {ItemGrantShare.RefusedWrites} write(s) refused.");

                Say($"story gates: brought along {GateFollow.Followed} time(s), " +
                    $"{GateFollow.StoryPortsReported} own story port(s) told to teammates; " +
                    $"story dialogues and waves held for a teammate's scene {StorySceneLease.Held} frame(s)" +
                    (StorySceneLease.Hooked ? "." : " (LEASE NOT PATCHED)."));

                Say($"engagements: {EngageWatch.Reported} transitions reported, " +
                    $"{EnemyEngagements.Held} dialogue hold(s) and {EnemyEngagements.Leases} brawl " +
                    $"lease(s) served here, {EnemyThreat.Pardoned} pardon(s).");

                Say($"joined brawls: invited into {TeamBrawls.Joined} of the host's, " +
                    $"{TeamBrawls.Assists} teammate blow(s) carried into this player's own.");

                // Printed even at zero. Asked at zero during brawls means the gate's patches are dead.
                Say($"brawl gate: asked {PuppetBrawlGate.Asked}, kept a remote player's body " +
                    $"out {PuppetBrawlGate.Refused} time(s).");

                Say($"teammate blows: {RemoteEnemyHealth.Refused} refused by this world's protections, " +
                    $"{RemoteEnemyHealth.BrawlSaves} brawl loser(s) knocked out instead of killed.");

                Say(WeatherSync.Describe());

                foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
                {
                    if (player.Key == CoopSession.LocalPlayerId)
                    {
                        Say($"  {player.Key}: {player.Value} (you)");
                        continue;
                    }

                    // Measured end to end, since the transport's ping only reaches the relay.
                    float? roundTrip = CoopSession.RoundTripMs(player.Key);
                    string latency = roundTrip is { } milliseconds ? $"{milliseconds:0} ms" : "measuring";

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
            if (!RemotePlayers.Bodies.TryGetValue(player, out RemotePlayer? body) || body == null) return;
            if (body.Body == null) return;

            string distance = PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor local)
                ? $"{Vector3.Distance(local.transform.position, body.Body.transform.position):0} units away"
                : "distance unknown";

            Say($"    {distance}, {body.DescribeStreaming()}");
        }
    }
}
