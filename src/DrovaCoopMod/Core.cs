using Drova_Modding_API.Access;
using Drova_Modding_API.GlobalFields;
using Drova_Modding_API.Systems.Testing;
using DrovaCoopMod.Session;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(DrovaCoopMod.Core), "Drova Coop", DrovaCoopMod.BuildInfo.Version, "Drova Modding", null)]
[assembly: MelonGame("Just2D", "Drova")]
// The assembly name, not the melon's display name: MelonLoader resolves this against loaded assemblies,
// and "Drova Modding API" left it reporting the dependency as missing on every start. Harmless today,
// an outright load failure in a future MelonLoader.
[assembly: MelonAdditionalDependencies("Drova_Modding_API")]

namespace DrovaCoopMod
{
    /// <summary>
    /// Entry point of the coop mod. Its job here is small on purpose: wire preferences, hand the session
    /// layer its lifecycle, and draw what the mod puts on screen. Everything that talks to the wire
    /// lives in <see cref="CoopSession"/>, and everything below that belongs to the Drova Modding API.
    /// </summary>
    public class Core : MelonMod
    {
        private InputAction? _windowAction;
        private bool _validatedDeclarations;

        /// <summary>
        /// Whether a world is up rather than the main menu. Set from the scene the game loads, and false
        /// again at the menu, so anything that must only touch save files while nothing holds them can
        /// ask.
        /// </summary>
        internal static bool InGameplay { get; private set; }

        /// <summary>Above this, a frame is long enough to have broken something that counts frames.</summary>
        private const float LongFrameSeconds = 1f;

        /// <summary>How many frames ran long enough to matter. For <c>coop_status</c>.</summary>
        internal static int LongFrames { get; private set; }

        /// <summary>The worst of them, in seconds. For <c>coop_status</c>.</summary>
        internal static float LongestFrame { get; private set; }

        /// <inheritdoc/>
        public override void OnInitializeMelon()
        {
            base.OnInitializeMelon();

            CoopPreferences.Initialize();

            // First, and on its own thread, so the answer is likely in before anyone can host. A build
            // withdrawn by the policy is refused where a session begins, not here.
            VersionCheck.Start();

            CoopSession.Initialize();
            CoopCommands.Register();

            // The host's way back to the party checkpoint, beside the game's own "Load latest".
            Drova_Modding_API.Access.PauseMenuAccess.AddButton("coop_checkpoint",
                () => "Back to party checkpoint",
                () => MelonLogger.Msg("[Coop] " + World.GameOver.BackToCheckpoint()),
                visible: () => CoopSession.State == CoopSessionState.Hosting && World.Checkpoint.AllowsGoingBack,
                interactable: () => World.Checkpoint.Committed != 0);

            // Before anything else has a chance to ask. Other mods query the API at their own init, and
            // one that asks before this registers is told it is playing alone - which for a mod that
            // spawns things is the wrong answer in the one direction that matters.
            Session.CoopProvider.Register();

            // Patching is safe here because it resolves types, which exist as soon as the assemblies are
            // loaded. Anything reaching for a game manager is not, and is deferred to the first scene.
            RegisterTestCommands();
            World.PausePolicy.Initialize(HarmonyInstance);
            World.DialogueWindowGuard.Initialize(HarmonyInstance);
            World.DamageWatch.Initialize(HarmonyInstance);
            World.DownedState.Initialize(HarmonyInstance);
            World.GVarWatch.Initialize(HarmonyInstance);
            World.StoryEffectGate.Initialize(HarmonyInstance);
            World.ResourceWork.Initialize(HarmonyInstance);
            World.GVarDerived.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Haul)) World.HaulShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.MineDay)) World.MineDayLoop.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.SceneMove)) World.SceneMoveWatch.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Clock)) World.ClockGuard.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Scaling)) World.EnemyScaling.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Backups)) SaveBackups.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Loot)) World.LootShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.WorldObjects)) World.WorldObjects.Initialize(HarmonyInstance);
            World.Trade.Initialize(HarmonyInstance);
            World.TeammateMenu.Initialize(HarmonyInstance);
            World.SpokenLines.Initialize(HarmonyInstance);
            World.FogletShare.Initialize(HarmonyInstance);
            World.FogletMinions.Initialize(HarmonyInstance);
            World.TrapShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Guard)) World.GuardReach.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Reach)) World.AnyPlayerReach.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Lease)) World.StorySceneLease.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Block)) World.BlockVerdicts.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Cache)) World.CacheShare.Initialize(HarmonyInstance);

            // Loading a savegame inside gameplay rewinds this machine's half of the world - every
            // variable, the journal, the lot - without ending the session. No join happens, so nothing
            // would ever put it back: the client would play on against a world the host had moved past,
            // silently. That is how a death and a reload used to cost a player their quest log.
            Drova_Modding_API.Systems.SaveGame.SaveGameSystem.AfterSaveGameLoaded += OnSaveGameLoaded;
            Drova_Modding_API.Systems.SaveGame.SaveGameSystem.BeforeSaveGameLoaded += _ => CoopSession.HostLoadStarted();
            World.CutsceneWatch.Initialize(HarmonyInstance);
            World.CutscenePorts.Initialize(HarmonyInstance);
            World.JournalShare.Initialize(HarmonyInstance);
            World.NpcPlacement.Initialize(HarmonyInstance);
            World.MapShare.Initialize(HarmonyInstance);
            World.RewardShare.Initialize(HarmonyInstance);
            World.ItemGrantShare.Initialize(HarmonyInstance);
            World.ItemUseWatch.Initialize(HarmonyInstance);
            World.StatusWatch.Initialize(HarmonyInstance);
            World.KnowledgeWatch.Initialize(HarmonyInstance);
            World.PuppetCrits.Initialize(HarmonyInstance);
            World.DownedTargeting.Initialize(HarmonyInstance);
            World.EnemyPerception.Initialize(HarmonyInstance);
            World.CameraReach.Initialize(HarmonyInstance);
            World.EnemyCognition.Initialize(HarmonyInstance);
            World.EnemyThreat.Initialize(HarmonyInstance);
            World.PuppetTargets.Initialize(HarmonyInstance);
            World.RemoteEnemyBlows.Initialize(HarmonyInstance);
            World.PuppetBrawlGate.Initialize(HarmonyInstance);
            World.HostWorldState.Initialize(HarmonyInstance);
            World.EnemyRebirth.Initialize(HarmonyInstance);
            World.ExperienceShare.Initialize(HarmonyInstance);
            World.SummonWatch.Initialize(HarmonyInstance);
            World.SpawnedEnemies.Initialize();
            World.RegionShare.Initialize();

            EnableWindowKey();
        }

        /// <inheritdoc/>
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            base.OnSceneWasLoaded(buildIndex, sceneName);

            // A session is only meaningful with a world loaded; the main menu has nothing to sync.
            //
            // Latched on the two scenes that actually mean something, never assigned from whichever scene
            // happened to load last. Drova streams area scenes continuously - Scene_OpenWorldRoot,
            // Scene_Creatures, Scene_CreaturesInsane and dozens of chunks - so testing the last-loaded
            // name against GameplayMain reads false within seconds of the world coming up, and stays
            // false. That silently disabled every per-frame update and, worse, made the teardown below fire on the
            // first streamed chunk: a live session would drop the moment the player walked somewhere.
            if (sceneName == SceneNames.GameplayMain)
            {
                InGameplay = true;

                // Once a world exists, so the variable database can be asked whether the names this
                // mod declares are names the game has. A declaration that no longer resolves degrades
                // silently to "ordinary variable", and the build before this one shipped exactly such
                // a typo with nothing anywhere saying so.
                if (!_validatedDeclarations)
                {
                    _validatedDeclarations = true;
                    World.GVarWatch.ValidateDeclarations();
                }
            }
            else if (sceneName == SceneNames.MainMenu)
            {
                InGameplay = false;
            }

            if (!InGameplay && CoopSession.State != CoopSessionState.Idle)
            {
                CoopSession.Leave();
            }
        }

        /// <summary>
        /// A savegame just streamed into this world, without the session noticing.
        ///
        /// Everything this mod remembers about what has already been said or heard is now keyed to a
        /// world that no longer exists: journal paths that were earned, dialogue rewards that were
        /// paid, NPCs that were placed. Forgetting it is the honest reaction - the dedup sets exist to
        /// stop a second copy of something this world holds, and this world holds something else now.
        /// Then the host is asked for its variables and its journal again, because nothing else would
        /// ever close the gap.
        /// </summary>
        private static void OnSaveGameLoaded(Il2CppDrova.Saveables.Savegame saveGame)
        {
            // Before anything else: a catch-up a client asked for meanwhile is read out of this world, which is now the
            // loaded one.
            CoopSession.HostLoadFinished();

            if (CoopSession.State == CoopSessionState.Hosting)
            {
                // The host's world went backwards too. Its own dedupe memory now describes a world
                // that no longer exists - above all what it last told the session about each
                // variable, which would swallow the host re-setting a flag it had set before the load.
                // Teammates keep what they hold; a finished quest there is never reopened by the
                // host's older copy (RemoteGVar).
                ResetWorldMemory(keepHaulBindings: true);

                bool partyLoad = World.GameOver.PartyLoading;
                World.GameOver.PartyLoading = false;
                if (partyLoad) return;

                // A save the party shares - a party slot, or a host save whose checkpoint round every machine wrote: everyone
                // goes back with the host, each to their own copy of that checkpoint, as on a lost run. Else the host alone
                // would stand in the past. A teammate whose copy has rotated out goes to their latest save.
                int loaded = saveGame?.MetaInfo?.GetIdentifier().SlotIndex ?? -1;
                if (World.Checkpoint.AllowsGoingBack && World.Checkpoint.TryGetRound(loaded, out ushort round))
                {
                    MelonLogger.Msg($"[Coop] the host went back to a save of checkpoint {round}; everyone goes back to it.");
                    CoopSession.ReportGameOver(round);
                    return;
                }

                MelonLogger.Warning("[Coop] the host loaded a savegame inside the session that no party checkpoint stands " +
                    "for (made before the session, or its round never completed). Teammates keep their progress; this " +
                    "world is now behind them. Restarting the session from this save is the clean way back to one world.");

                return;
            }

            if (CoopSession.State != CoopSessionState.Joined) return;

            // Which variable counts which rock face's piles is kept: that is a fact about how the world
            // was authored, not about this session, and re-learning it costs a swing at every spot.
            ResetWorldMemory(keepHaulBindings: true);

            MelonLogger.Msg("[Coop] a savegame was loaded inside the session; this world has gone " +
                "backwards. Asking the host for its variables and journal again.");

            CoopSession.RequestResync(Messages.GVarResyncRequestMessage.ReasonSavegameLoaded, false);
        }

        /// <summary>
        /// Forgets what this machine has said or heard about the world it is in. Shared by a savegame
        /// loaded inside a session and by leaving the world altogether.
        /// </summary>
        /// <param name="keepHaulBindings">Whether to keep which variable counts which rock face's piles.</param>
        private static void ResetWorldMemory(bool keepHaulBindings)
        {
            World.RemoteGVar.Reset();
            World.JournalShare.Reset();
            World.RewardShare.Reset();
            World.ItemGrantShare.Reset();
            World.NpcPlacement.Reset();
            World.GateFollow.Reset();
            World.ResourceWork.Reset();
            World.LootShare.Reset();
            World.WorldObjects.Reset();
            World.GuardReach.Reset();
            World.AnyPlayerReach.Reset();
            World.StorySceneLease.Reset();
            World.LedgerStore.Reset();
            World.BlockVerdicts.Reset();
            World.Arena.Reset();
            World.Trade.Reset();
            World.FogletShare.Reset();
            World.CacheShare.Reset();
            World.GVarDerived.Reset();
            World.HaulShare.Reset(keepHaulBindings);
            World.MineDayLoop.Reset();
            World.ClockGuard.Reset();
            World.SpokenLines.Reset();
            World.TrapShare.Clear();
            World.LeaseSummons.Clear();
            World.RegionShare.Clear();
            World.ArenaTeammates.Clear();
            World.ArenaGates.Clear();
        }

        /// <summary>
        /// What the API's test agent needs from this mod: hosting and joining, the session in <c>state</c>,
        /// and whether the host is steering a creature here. Inert unless the game runs with --agent-port.
        /// </summary>
        private static void RegisterTestCommands()
        {
            TestAgent.RegisterCommand("host", _ => CoopActions.Host());
            // The host's loot rule, set before hosting - it is sent with the session.
            TestAgent.RegisterCommand("lootmode", mode =>
            {
                CoopPreferences.SharedLoot = mode.Trim().Equals("shared", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.SharedLoot ? "shared" : "instanced";
            });
            // The host's friendly-fire rule. Unlike the loot rule it may change mid-session: the host sends it on.
            TestAgent.RegisterCommand("ff", mode =>
            {
                CoopPreferences.FriendlyFire = mode.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.FriendlyFire ? "on" : "off";
            });
            // The host's creature health rule in percent; a preference, so a scenario that raises it sets it back.
            TestAgent.RegisterCommand("enemyhealth", percent =>
            {
                CoopPreferences.EnemyHealthPercent = ushort.Parse(percent.Trim());
                return CoopPreferences.EnemyHealthPercent.ToString();
            });
            // Sets the loaded savegame's game mode, so a Hardcore run can be tested on a throwaway hero.
            TestAgent.RegisterCommand("gamemode", name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return World.Checkpoint.Mode.ToString();
                Il2CppDrova.GameMode mode = Enum.Parse<Il2CppDrova.GameMode>(name.Trim(), ignoreCase: true);
                if (!Il2CppDrova.SavegameGameHandler.TryGet(out var saves) || saves?.CurrentSavegame?.Data == null) throw new InvalidOperationException("no savegame loaded");
                Il2Cpp.SavegameDataExtensions.SetGameMode(saves.CurrentSavegame.Data, mode);
                // The death screen reads Savegame.Current, which need not be the handler's object.
                if (Il2CppDrova.Saveables.Savegame.Current?.Data is { } current) Il2Cpp.SavegameDataExtensions.SetGameMode(current, mode);
                return World.Checkpoint.Mode.ToString();
            });
            TestAgent.RegisterCommand("join", code => CoopActions.Join(code));
            TestAgent.RegisterCommand("steered", guid =>
                Il2CppDrova.EntityGameHandler.TryGet(out Il2CppDrova.EntityGameHandler handler)
                    && handler?.GetActorFromGuid(guid.Trim()) is { } actor && World.RemoteEnemies.IsSteered(actor) ? "1" : "0");
            TestAgent.RegisterCommand("teammate", _ =>
            {
                if (!Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self)) throw new InvalidOperationException("no player yet");

                float nearest = float.MaxValue;
                foreach (Vector2 position in World.RemotePlayers.Positions)
                {
                    nearest = Math.Min(nearest, (position - self.GetFeetPosition()).magnitude);
                }

                // Also where the teammate says it stands: a body far outside this world's loaded area can be
                // left behind at its last loaded spot, and the two answers differing is worth seeing.
                float reported = float.MaxValue;
                foreach (Messages.PlayerStateMessage state in CoopSession.LatestStates.Values)
                {
                    reported = Math.Min(reported, (new Vector2(state.X, state.Y) - self.GetFeetPosition()).magnitude);
                }

                return nearest == float.MaxValue ? "none" : "dist=" + nearest.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                    " reported=" + (reported == float.MaxValue ? "-" : reported.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            });
            // Plain numbers for assertions: "counter held >= 1" rather than parsing coop_status prose.
            TestAgent.RegisterCommand("counter", name => name.Trim().ToLowerInvariant() switch
            {
                "held" => World.StorySceneLease.Held.ToString(),
                "followed" => World.GateFollow.Followed.ToString(),
                "storyports" => World.GateFollow.StoryPortsReported.ToString(),
                "reach" => World.AnyPlayerReach.Answered.ToString(),
                "regressions" => World.RemoteGVar.RegressionsRefused.ToString(),
                "mirrored" => World.GVarDerived.MirroredSkipped.ToString(),
                "handedin" => World.ItemGrantShare.HandedIn.ToString(),
                "credited" => World.RemoteEnemyDeath.Credited.ToString(),
                "bedmoved" => World.MineDayLoop.Moved.ToString(),
                "worked" => World.ResourceWork.Worked.ToString(),
                "workapplied" => World.ResourceWork.Applied.ToString(),
                "leases" => World.EnemyEngagements.Leases.ToString(),
                "dmgsent" => World.DamageWatch.ReportedPlayerBlows.ToString(),
                "dmgapplied" => World.RemoteDamage.Applied.ToString(),
                "copyhits" => World.DamageWatch.CopyBlowsOnLocal.ToString(),
                "blockbroken" => World.BlockVerdicts.Sent.ToString(),
                "blockshown" => World.BlockVerdicts.Applied.ToString(),
                "summonshared" => World.SummonWatch.Shared.ToString(),
                "summonpaired" => World.SummonWatch.Paired.ToString(),
                "cutports" => World.CutscenePorts.Applied.ToString(),
                "keptplaying" => World.RemoteEnemy.KeptPlaying.ToString(),
                "idlecleared" => World.RemoteEnemy.IdleCleared.ToString(),
                "echoignored" => World.RemoteGVar.EchoesIgnored.ToString(),
                "standretry" => World.DownedState.StandRetries.ToString(),
                "lootsent" => World.LootShare.Sent.ToString(),
                "lootapplied" => World.LootShare.Applied.ToString(),
                "chestoffered" => World.LootShare.Proposed.ToString(),
                "bodiesnamed" => World.LootShare.BodiesNamed.ToString(),
                "cachesent" => World.CacheShare.Sent.ToString(),
                "cacheapplied" => World.CacheShare.Applied.ToString(),
                "scaled" => World.EnemyScaling.Scaled.ToString(),
                "backups" => SaveBackups.Made.ToString(),
                "savemarks" => CoopSaveMarks.Marked.ToString(),
                "choicesclosed" => World.SharedChoices.Closed.ToString(),
                "gameovers" => World.GameOver.Count.ToString(),
                "checkpoint" => World.Checkpoint.Committed.ToString(),
                "worldsent" => World.WorldObjects.Sent.ToString(),
                "worldapplied" => World.WorldObjects.Applied.ToString(),
                "tradedone" => World.Trade.Completed.ToString(),
                "traderefused" => World.Trade.Refused.ToString(),
                "menuchosen" => World.TeammateMenu.Chosen.ToString(),
                "invitesaccepted" => World.Invites.Accepted.ToString(),
                "fogletwoken" => World.FogletShare.Woken.ToString(),
                "bledout" => World.DownedState.BledOut.ToString(),
                "statusended" => World.StatusWatch.EndedApplied.ToString(),
                "linessent" => World.SpokenLines.Sent.ToString(),
                "linesshown" => World.SpokenLines.Shown.ToString(),
                "bodylines" => World.SpokenLines.BodyShown.ToString(),
                "voiced" => World.SpokenLines.Voiced.ToString(),
                "boolops" => World.GVarDerived.BoolOperations.ToString(),
                "gintdup" => World.GVarDerived.OperationsSkipped.ToString(),
                "spawnshared" => World.FogletMinions.Shared.ToString(),
                "spawnsannounced" => World.SpawnedEnemies.AnnouncedCount.ToString(),
                "spawnsbuilt" => World.SpawnedEnemies.BuiltCount.ToString(),
                "spawnskipped" => World.FogletMinions.Skipped.ToString(),
                "conjurecast" => World.FogletMinions.Cast.ToString(),
                "traplaid" => World.TrapShare.Laid.ToString(),
                "summonsbuilt" => World.LeaseSummons.Built.ToString(),
                "regionstaken" => World.RegionShare.Taken.ToString(),
                "arenajoined" => World.ArenaTeammates.Joined.ToString(),
                "arenako" => World.ArenaTeammates.KnockedOut.ToString(),
                "summonsbound" => World.LeaseSummons.Bound.ToString(),
                "trapshown" => World.TrapShare.Shown.ToString(),
                "trapwithdrawn" => World.TrapShare.Withdrawn.ToString(),
                "gatesapplied" => World.ArenaGates.Applied.ToString(),
                "leasemirrored" => World.LeaseMirror.Mirrored.ToString(),
                "arenarefunded" => World.Arena.Refunded.ToString(),
                "copyeffects" => World.StatusWatch.CopyEffectsRefused.ToString(),
                _ => throw new InvalidOperationException("counters: held, followed, storyports, reach, regressions, mirrored, handedin, " +
                    "credited, bedmoved, worked, workapplied, leases, dmgsent, dmgapplied, copyhits, blockbroken, blockshown, summonshared, summonpaired, copyeffects, cutports, keptplaying, idlecleared, echoignored, standretry, lootsent, lootapplied, chestoffered, bodiesnamed, cachesent, cacheapplied, scaled, backups, savemarks, choicesclosed, gameovers, checkpoint, worldsent, worldapplied, tradedone, traderefused, menuchosen, invitesaccepted, fogletwoken, spawnshared, spawnskipped, bledout, statusended, linessent, linesshown, conjurecast, bodylines, voiced, leasemirrored, arenarefunded, traplaid, trapshown, trapwithdrawn, gatesapplied, boolops, gintdup"),
            });
            TestAgent.RegisterState(() => $"session={CoopSession.State} code={CoopSession.SessionCode ?? "-"} " +
                $"players={CoopSession.Players.Count} ff={(CoopSession.FriendlyFire ? "on" : "off")}");
        }

        /// <inheritdoc/>
        public override void OnUpdate()
        {
            base.OnUpdate();

            // Every timer in this mod is measured in frames, and that only works while a frame is
            // roughly a frame. On 2026-08-09 a host frame aged four dialogue holds past a fifteen-second
            // expiry in a single step, three milliseconds after creating them - which is also the proof
            // that Unity's own maximum-delta clamp is not protecting these clocks. Said once per long
            // frame, at Release level, in every mode: a session that never prints this is a session in
            // which none of the clamps downstream could have mattered.
            float frame = Time.unscaledDeltaTime;

            if (frame > LongFrameSeconds)
            {
                LongFrames++;
                if (frame > LongestFrame) LongestFrame = frame;

                MelonLogger.Warning($"[Coop] a frame here took {frame:0.0}s. Anything this mod was " +
                    "waiting for has been told that nothing happened, because nothing was observed.");
            }

            // Ahead of the gameplay gate on purpose: a join already in flight has a deadline to meet, and
            // unscaled time so that an open menu does not stop the clock the transport is still running on.
            CoopSession.Tick(Time.unscaledDeltaTime);

            // Ahead of the gate as well: the window is how a session is started, and that happens in the
            // main menu as much as in a world.
            if (_windowAction != null && _windowAction.WasReleasedThisFrame())
            {
                LoggerInstance.Msg("[Coop] " + UI.SessionWindow.Toggle());
            }

            // Ahead of the gameplay gate too: a world left frozen is exactly the state in which nothing
            // else runs, so the switch that unfreezes it cannot sit behind another condition.
            World.PausePolicy.Tick(Time.unscaledDeltaTime);

            if (!InGameplay)
            {
                // The bodies belong to the world that just went away; holding references to destroyed
                // objects across a scene change is how a mod starts logging null reference exceptions
                // from somewhere unrelated.
                World.RemotePlayers.Clear();
                World.NameTags.Reset();
                World.EquipmentBroadcaster.Reset();
                World.TalentBroadcaster.Reset();
                World.ItemUseWatch.Reset();
                World.RemoteEnemyDeath.Clear();
                World.RemoteEnemyHealth.Clear();
                World.EnemyHealthBroadcaster.Clear();
                World.EnemyBroadcaster.Clear();
                World.LeaseMirror.Clear();

                // The index holds proxies over the pooler's action prefabs, and the pooler belongs to
                // the world that just ended. Rebuilt lazily from the next world's own database.
                World.ActionPrefabIndex.Clear();

                // Lets go of every creature the host was holding loaded. Pinning without unpinning is a
                // host whose memory grows for as long as it runs.
                World.EnemyPinning.Clear();
                World.EnemyPerception.Reset();
                World.CameraReach.Reset();
                World.CutsceneWatch.Reset();
                World.RemoteCutscene.Reset();
                World.CutscenePorts.Reset();
                World.WeatherSync.Reset();
                World.DaytimeSync.Reset();
                World.MapShare.Reset();
                ResetWorldMemory(keepHaulBindings: false);
                World.SceneMoveWatch.Reset();
                World.EnemyDeathWatch.ResetCounters();

                LongFrames = 0;
                LongestFrame = 0f;
                World.EnemyCognition.Reset();
                World.EnemyThreat.Reset();
                World.EngageWatch.Reset();
                World.EnemyEngagements.Reset();
                World.TeamBrawls.Reset();
                World.PuppetBrawlGate.Reset();
                World.RemoteEnemyHealth.Reset();
                World.PuppetTargets.Reset();
                World.RemoteEnemyBlows.Reset();
                World.HostWorldState.Clear();
                World.EnemyRebirth.Clear();
                World.ExperienceShare.Clear();
                World.SummonWatch.Clear();
                World.SpawnedEnemies.Clear();

                // Hands every driven creature back to its own AI. One left switched off is inert forever,
                // which is a camp standing perfectly still as the player walks through it.
                World.RemoteEnemies.Clear();

                // Hands every write-protected creature back before the world goes. A body that outlived
                // the session invincible is the one failure here a player would carry into a solo save.
                World.EnemyRegistry.Clear();
                return;
            }

            // First of everything in the world, because write-protection installed after something has
            // already been hit is a number this machine decided and the host will never hear about.
            World.EnemyRegistry.Update(Time.unscaledDeltaTime);

            // Asks the host about creatures this client's own save calls dead. Kept next to the registry
            // because the answers arrive as graveyard entries, which the registry is what applies.
            World.HostWorldState.Update(Time.unscaledDeltaTime);

            // Straight after, because what it acts on is what the registry decided this frame: a creature the
            // host has standing that this world has lying down. It destroys and rebuilds bodies, so it has to
            // run before anything below reaches for one - the registry re-resolves by guid every frame and
            // will hand out the new body on the next pass.
            World.EnemyRebirth.Update(Time.unscaledDeltaTime);

            // Beside the spawn replication, and for the same job on the other kind of runtime creature: what
            // a spell conjures already exists on both machines, so what it needs is a shared name rather
            // than a body. Before the drivers below, so a summon named this frame is driven this frame.
            World.SummonWatch.Update(Time.unscaledDeltaTime);

            // Before the registry's client half acts on binds, so a body announced this frame exists by
            // the time anything tries to drive it.
            World.SpawnedEnemies.Update(Time.unscaledDeltaTime);

            // Ahead of the broadcast, so a player put on the floor this frame sends the health that
            // decision left them on rather than the figure that would have killed them.
            World.DownedState.Update(Time.unscaledDeltaTime);
            World.GameOver.Update();
            World.Checkpoint.Update(Time.unscaledDeltaTime);

            // Between the two, deliberately. Applying a blow after the downed check would let a player be
            // knocked down a frame late, and applying it after the broadcast would send the health they
            // had before the hit - so the far end sees its own swing land one tick later than it did.
            World.RemoteDamage.Update();

            // After the damage, so a poison that arrives alongside the blow that delivered it starts
            // ticking from the health that blow left rather than from the health before it.
            World.RemoteStatus.Update();

            // Quest and world flags the session reported. Order does not matter to these: a variable
            // write stands alone, and SetValue itself refuses one the world is not ready for.
            World.RemoteGVar.Update();

            // Shared story cutscenes, after the flags: a transition often turns its variables in the
            // same breath, and the state should be there before the scene plays over it.
            World.RemoteCutscene.Update(Time.unscaledDeltaTime);
            World.CutscenePorts.Update();

            // The host reports its sky, a client glides toward it. Order-free: it touches nothing but
            // the weather parameters.
            World.WeatherSync.Update(Time.unscaledDeltaTime);

            // Journal entries the session shared. After the variables, so an entry whose text reads
            // quest values reads the ones that arrived with it.
            World.ResourceWork.Update();
            World.LootShare.Update();
            World.WorldObjects.Update();
            World.GuardReach.Update();
            World.AnyPlayerReach.Update();

            // Tried every frame rather than once when hosting began: a session can be started from the
            // main menu, and the savegame this world is meant to remember does not exist until a world
            // does. It returns immediately once it has read one.
            World.LedgerStore.Load();
            World.Arena.Update(Time.unscaledDeltaTime);
            World.Arena.Tick(Time.unscaledDeltaTime);
            World.Trade.Update();
            World.TeammateMenu.Update();

            // The haul, after the swings that drop the piles and after the variables have settled, so a
            // pile this frame produced is findable and the tally it belongs to is where it will stay.
            World.HaulShare.Update();

            // The bound the game's own cutscene move sets and then never enforces. Order-free: it only
            // ever finishes a timer that has already outlived twice its authored limit.
            World.SceneMoveWatch.Update(Time.unscaledDeltaTime);

            World.JournalShare.Update();

            // Where the host's world has the NPCs standing around this player. Order-free.
            World.NpcPlacement.Update(Time.unscaledDeltaTime);

            // The host's clock. Before nothing in particular: the corrections are rare and the
            // day-forward path is the game's own.
            World.DaytimeSync.Update(Time.unscaledDeltaTime);

            // Maps a teammate was handed. Order-free: an unlock stands alone.
            World.MapShare.Update();

            // Dialogue rewards a teammate was paid. Order-free for the same reason.
            World.RewardShare.Update();

            // Items a teammate's dialogue handed out. Order-free for the same reason.
            World.ItemGrantShare.Update();

            // After the variable applies that arm it: a gate arriving this frame starts its grace
            // this frame.
            World.GateFollow.Update(Time.unscaledDeltaTime);

            // What this player is personally doing with the host's NPCs, and the host's answer to it.
            // Before the enemy appliers, so an NPC held for a dialogue stops being driven the same
            // frame the hold lands.
            World.EngageWatch.Update(Time.unscaledDeltaTime);
            World.EnemyEngagements.Update(Time.unscaledDeltaTime);
            World.TeamBrawls.Update(Time.unscaledDeltaTime);

            // Before the damage appliers, so a blow lands on a body that is already where the host says
            // it is rather than where it was a frame ago.
            World.RemoteEnemies.Update(Time.unscaledDeltaTime);

            // Health before death, so a creature whose last blow arrives as a figure and then as a death
            // does not have the death land on a body that is about to be told it has health.
            World.RemoteEnemyHealth.Update();

            // Before the reported deaths, so a creature whose payout and death arrive in the same tick is
            // paid at the figure the machine that killed it calculated, and this machine's own death path
            // then finds it already settled.
            World.ExperienceShare.Update();

            // After both, so a creature the other player killed is dead before anything on this machine
            // reads its health or asks it to swing.
            World.RemoteEnemyDeath.Update(Time.unscaledDeltaTime);

            // After the host has applied this frame's reported blows, so what goes out is the health those
            // blows produced rather than the health before them.
            World.EnemyHealthBroadcaster.Update(Time.unscaledDeltaTime);
            World.EnemyBroadcaster.Update(Time.unscaledDeltaTime);
            World.LeaseMirror.Update(Time.unscaledDeltaTime);
            World.TrapShare.Update();
            World.LeaseSummons.Update();
            World.RegionShare.Update(Time.unscaledDeltaTime);
            World.ArenaTeammates.Update();
            World.ArenaGates.Update(Time.unscaledDeltaTime);

            // After the registry has decided what the host owns, because that set is what gets held.
            World.EnemyPinning.Update(Time.unscaledDeltaTime);

            // Order does not matter here the way it does above - nothing else reads a recipe book - so it
            // sits with the other appliers rather than anywhere in particular.
            World.RemoteKnowledge.Update();

            World.LocalPlayerBroadcaster.Update(Time.unscaledDeltaTime);
            World.EquipmentBroadcaster.Update(Time.unscaledDeltaTime);
            World.TalentBroadcaster.Update(Time.unscaledDeltaTime);
            World.RemotePlayers.Update(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Binds the configured key to the session window. A path that does not parse is logged and the
        /// key stays off, since <c>coop_window</c> still opens it.
        /// </summary>
        private void EnableWindowKey()
        {
            string path = CoopPreferences.WindowKey.Trim();
            if (path.Length == 0) return;

            try
            {
                _windowAction = new InputAction("CoopWindow", InputActionType.Button, path);
                _windowAction.Enable();
            }
            catch (Exception e)
            {
                _windowAction = null;
                LoggerInstance.Warning($"[Coop] WindowKey '{path}' is not a control path the input system accepts; " +
                    "the session window opens with coop_window only. " + e.Message);
            }
        }

        /// <inheritdoc/>
        public override void OnGUI()
        {
            base.OnGUI();
            World.NameTags.Draw();

            // After the name tags, so the window draws over them.
            UI.SessionWindow.Draw();
        }

        /// <inheritdoc/>
        public override void OnDeinitializeMelon()
        {
            base.OnDeinitializeMelon();

            // Before the session goes, so a mod asking during its own shutdown is told it is alone rather
            // than reading half-torn-down session state.
            Session.CoopProvider.Unregister();

            CoopSession.Shutdown();
        }
    }
}
