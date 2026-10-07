using Drova_Modding_API.Access;
using Drova_Modding_API.GlobalFields;
using Drova_Modding_API.Systems.Testing;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(DrovaCoopMod.Core), "Drova Coop", DrovaCoopMod.BuildInfo.Version, "Drova Modding", null)]
[assembly: MelonGame("Just2D", "Drova")]
// The assembly name, not the melon's display name: MelonLoader resolves this against loaded assemblies.
[assembly: MelonAdditionalDependencies("Drova_Modding_API")]

namespace DrovaCoopMod
{
    /// <summary>
    /// Entry point of the coop mod: wires preferences and patches, drives the per-frame updates and draws the UI.
    /// </summary>
    public class Core : MelonMod
    {
        private InputAction? _windowAction;
        private bool _validatedDeclarations;

        /// <summary>
        /// Whether a world is up rather than the main menu.
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

            // First, so the answer is likely in before anyone can host.
            VersionCheck.Start();

            CoopSession.Initialize();
            CoopCommands.Register();

            // The host's way back to the party checkpoint, beside the game's own "Load latest".
            PauseMenuAccess.AddButton("coop_checkpoint",
                () => "Back to party checkpoint",
                () => MelonLogger.Msg("[Coop] " + World.GameOver.BackToCheckpoint()),
                visible: () => CoopSession.State == CoopSessionState.Hosting && World.Checkpoint.AllowsGoingBack,
                interactable: () => World.Checkpoint.Committed != 0);

            // Early, since other mods query the API at their own init.
            CoopProvider.Register();

            RegisterTestCommands();
            if (HookSwitches.IsOn(nameof(World.PausePolicy))) World.PausePolicy.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.DialogueWindowGuard))) World.DialogueWindowGuard.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.DamageWatch))) World.DamageWatch.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.DownedState))) World.DownedState.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.GVarWatch))) World.GVarWatch.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.StoryEffectGate))) World.StoryEffectGate.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.ResourceWork))) World.ResourceWork.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.GVarDerived))) World.GVarDerived.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Haul)) World.HaulShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.MineDay)) World.MineDayLoop.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.SceneMove)) World.SceneMoveWatch.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Clock)) World.ClockGuard.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Scaling)) World.EnemyScaling.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Backups)) SaveBackups.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Loot)) World.LootShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.WorldObjects)) World.WorldObjects.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.Trade))) World.Trade.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.TeammateMenu))) World.TeammateMenu.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.SpokenLines))) World.SpokenLines.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.FogletShare))) World.FogletShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.FogletMinions))) World.FogletMinions.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.TrapShare))) World.TrapShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Guard)) World.GuardReach.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Reach)) World.AnyPlayerReach.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Lease)) World.StorySceneLease.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Block)) World.BlockVerdicts.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(HookSwitches.Cache)) World.CacheShare.Initialize(HarmonyInstance);

            // Loading a savegame inside gameplay rewinds this world without ending the session, so it must resync.
            Drova_Modding_API.Systems.SaveGame.SaveGameSystem.AfterSaveGameLoaded += OnSaveGameLoaded;
            Drova_Modding_API.Systems.SaveGame.SaveGameSystem.BeforeSaveGameLoaded += _ =>
            {
                CoopSession.HostLoadStarted();

                // Refunds and trade undos write inventories, so they run on the world being left, not the loaded one.
                World.Arena.Reset();
                World.Trade.Reset();
            };
            if (HookSwitches.IsOn(nameof(World.CutsceneWatch))) World.CutsceneWatch.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.CutscenePorts))) World.CutscenePorts.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.JournalShare))) World.JournalShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.NpcPlacement))) World.NpcPlacement.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.MapShare))) World.MapShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.RewardShare))) World.RewardShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.ItemGrantShare))) World.ItemGrantShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.ItemUseWatch))) World.ItemUseWatch.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.StatusWatch))) World.StatusWatch.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.KnowledgeWatch))) World.KnowledgeWatch.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.PuppetCrits))) World.PuppetCrits.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.DownedTargeting))) World.DownedTargeting.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.EnemyPerception))) World.EnemyPerception.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.CameraReach))) World.CameraReach.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.EnemyCognition))) World.EnemyCognition.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.EnemyThreat))) World.EnemyThreat.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.PuppetTargets))) World.PuppetTargets.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.RemoteEnemyBlows))) World.RemoteEnemyBlows.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.PuppetBrawlGate))) World.PuppetBrawlGate.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.HostWorldState))) World.HostWorldState.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.EnemyRebirth))) World.EnemyRebirth.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.ExperienceShare))) World.ExperienceShare.Initialize(HarmonyInstance);
            if (HookSwitches.IsOn(nameof(World.SummonWatch))) World.SummonWatch.Initialize(HarmonyInstance);
            World.SpawnedEnemies.Initialize();
            World.RegionShare.Initialize();

            EnableWindowKey();
        }

        /// <inheritdoc/>
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            base.OnSceneWasLoaded(buildIndex, sceneName);

            // Latched on these two scenes only, since Drova streams area chunks as scenes continuously.
            if (sceneName == SceneNames.GameplayMain)
            {
                InGameplay = true;

                // Needs the variable database, so once a world exists. A bad name would degrade silently.
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
        /// A savegame loaded inside the session. Forgets the dedup memory of the old world, then a client
        /// asks the host for its variables and journal again.
        /// </summary>
        private static void OnSaveGameLoaded(Il2CppDrova.Saveables.Savegame saveGame)
        {
            // First, so a catch-up a client asked for meanwhile is read from the loaded world.
            CoopSession.HostLoadFinished();

            // Read and cleared on every load, or a client's or a solo load leaves it set for a later session.
            bool partyLoad = World.GameOver.PartyLoading;
            World.GameOver.PartyLoading = false;

            if (CoopSession.State == CoopSessionState.Hosting)
            {
                // Otherwise the host's memory would swallow re-setting a flag it set before the load.
                // Teammates keep their progress (RemoteGVar).
                ResetWorldMemory(keepHaulBindings: true);
                if (partyLoad) return;

                // A save of a party checkpoint takes everyone back with the host, each to their own copy.
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

            // Haul bindings are authored facts, not session state, so they survive.
            ResetWorldMemory(keepHaulBindings: true);

            MelonLogger.Msg("[Coop] a savegame was loaded inside the session; this world has gone " +
                "backwards. Asking the host for its variables and journal again.");

            CoopSession.RequestResync(Messages.GVarResyncRequestMessage.ReasonSavegameLoaded, false);
        }

        /// <summary>
        /// Forgets what this machine has said or heard about the world it is in.
        /// </summary>
        /// <param name="keepHaulBindings">Whether to keep which variable counts which rock face's piles.</param>
        private static void ResetWorldMemory(bool keepHaulBindings)
        {
            // The loaded save says who is dead again; a creature killed after it is alive in it.
            CoopSession.ForgetDeaths();
            World.EnemyRegistry.ForgetGraveyard();
            World.HostWorldState.ForgetAnswered();

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
        /// The test agent's commands for this mod. Inert unless the game runs with --agent-port.
        /// </summary>
        private static void RegisterTestCommands()
        {
            TestAgent.RegisterCommand("host", _ => CoopActions.Host());
            // The host's loot rule, set before hosting.
            TestAgent.RegisterCommand("lootmode", mode =>
            {
                CoopPreferences.SharedLoot = mode.Trim().Equals("shared", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.SharedLoot ? "shared" : "instanced";
            });
            // The host's friendly-fire rule. May change mid-session.
            TestAgent.RegisterCommand("ff", mode =>
            {
                CoopPreferences.FriendlyFire = mode.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
                return CoopPreferences.FriendlyFire ? "on" : "off";
            });
            // A saved preference, so a scenario that raises it sets it back.
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
                if (!Il2CppDrova.SavegameGameHandler.TryGet(out SavegameGameHandler? saves) || saves?.CurrentSavegame?.Data == null) throw new InvalidOperationException("no savegame loaded");
                Il2Cpp.SavegameDataExtensions.SetGameMode(saves.CurrentSavegame.Data, mode);
                // The death screen reads Savegame.Current, which need not be the handler's object.
                if (Il2CppDrova.Saveables.Savegame.Current?.Data is { } current) Il2Cpp.SavegameDataExtensions.SetGameMode(current, mode);
                return World.Checkpoint.Mode.ToString();
            });
            // Reads a CoopAccess.ShareHostSetting value; with a value, enforces it here (on the host: the session's value).
            TestAgent.RegisterCommand("hostsetting", args =>
            {
                string[] parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                Drova_Modding_API.Systems.Coop.HostSetting setting = Drova_Modding_API.Access.CoopAccess.HostSettings.FirstOrDefault(s => s.Key == parts[0])
                    ?? throw new InvalidOperationException("no shared setting " + parts[0]);
                if (parts.Length > 1) setting.Enforce(parts[1]);
                return setting.Read();
            });
            TestAgent.RegisterCommand("join", code => CoopActions.Join(code));
            TestAgent.RegisterCommand("leave", _ => CoopActions.Leave());
            TestAgent.RegisterCommand("steered", guid =>
                Il2CppDrova.EntityGameHandler.TryGet(out Il2CppDrova.EntityGameHandler handler)
                    && handler?.GetActorFromGuid(guid.Trim()) is { } actor && World.RemoteEnemies.IsSteered(actor) ? "1" : "0");
            TestAgent.RegisterCommand("teammate", _ =>
            {
                if (!PlayerAccess.TryGetPlayer(out Il2CppDrova.Actor self)) throw new InvalidOperationException("no player yet");

                float nearest = float.MaxValue;
                foreach (Vector2 position in World.RemotePlayers.Positions)
                {
                    nearest = Math.Min(nearest, (position - self.GetFeetPosition()).magnitude);
                }

                // Also the reported spot, since a body outside the loaded area can lag behind it.
                float reported = float.MaxValue;
                foreach (Messages.PlayerStateMessage state in CoopSession.LatestStates.Values)
                {
                    reported = Math.Min(reported, (new Vector2(state.X, state.Y) - self.GetFeetPosition()).magnitude);
                }

                return nearest == float.MaxValue ? "none" : "dist=" + nearest.ToString("0", System.Globalization.CultureInfo.InvariantCulture) +
                    " reported=" + (reported == float.MaxValue ? "-" : reported.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            });
            // Plain numbers for assertions: "counter held >= 1" rather than parsing coop_status prose.
            var counters = new Dictionary<string, Func<int>>
            {
                ["held"] = () => World.StorySceneLease.Held,
                ["followed"] = () => World.GateFollow.Followed,
                ["storyports"] = () => World.GateFollow.StoryPortsReported,
                ["reach"] = () => World.AnyPlayerReach.Answered,
                ["regressions"] = () => World.RemoteGVar.RegressionsRefused,
                ["mirrored"] = () => World.GVarDerived.MirroredSkipped,
                ["handedin"] = () => World.ItemGrantShare.HandedIn,
                ["credited"] = () => World.RemoteEnemyDeath.Credited,
                ["bedmoved"] = () => World.MineDayLoop.Moved,
                ["worked"] = () => World.ResourceWork.Worked,
                ["workapplied"] = () => World.ResourceWork.Applied,
                ["leases"] = () => World.EnemyEngagements.Leases,
                ["dmgsent"] = () => World.DamageWatch.ReportedPlayerBlows,
                ["dmgapplied"] = () => World.RemoteDamage.Applied,
                ["copyhits"] = () => World.DamageWatch.CopyBlowsOnLocal,
                ["blockbroken"] = () => World.BlockVerdicts.Sent,
                ["blockshown"] = () => World.BlockVerdicts.Applied,
                ["summonshared"] = () => World.SummonWatch.Shared,
                ["summonpaired"] = () => World.SummonWatch.Paired,
                ["cutports"] = () => World.CutscenePorts.Applied,
                ["keptplaying"] = () => World.RemoteEnemy.KeptPlaying,
                ["idlecleared"] = () => World.RemoteEnemy.IdleCleared,
                ["echoignored"] = () => World.RemoteGVar.EchoesIgnored,
                ["standretry"] = () => World.DownedState.StandRetries,
                ["lootsent"] = () => World.LootShare.Sent,
                ["lootapplied"] = () => World.LootShare.Applied,
                ["chestoffered"] = () => World.LootShare.Proposed,
                ["bodiesnamed"] = () => World.LootShare.BodiesNamed,
                ["cachesent"] = () => World.CacheShare.Sent,
                ["cacheapplied"] = () => World.CacheShare.Applied,
                ["scaled"] = () => World.EnemyScaling.Scaled,
                ["backups"] = () => SaveBackups.Made,
                ["savemarks"] = () => CoopSaveMarks.Marked,
                ["choicesclosed"] = () => World.SharedChoices.Closed,
                ["gameovers"] = () => World.GameOver.Count,
                ["checkpoint"] = () => World.Checkpoint.Committed,
                ["worldsent"] = () => World.WorldObjects.Sent,
                ["worldapplied"] = () => World.WorldObjects.Applied,
                ["tradedone"] = () => World.Trade.Completed,
                ["traderefused"] = () => World.Trade.Refused,
                ["menuchosen"] = () => World.TeammateMenu.Chosen,
                ["invitesaccepted"] = () => World.Invites.Accepted,
                ["fogletwoken"] = () => World.FogletShare.Woken,
                ["bledout"] = () => World.DownedState.BledOut,
                ["statusended"] = () => World.StatusWatch.EndedApplied,
                ["linessent"] = () => World.SpokenLines.Sent,
                ["linesshown"] = () => World.SpokenLines.Shown,
                ["bodylines"] = () => World.SpokenLines.BodyShown,
                ["voiced"] = () => World.SpokenLines.Voiced,
                ["boolops"] = () => World.GVarDerived.BoolOperations,
                ["gintdup"] = () => World.GVarDerived.OperationsSkipped,
                ["spawnshared"] = () => World.FogletMinions.Shared,
                ["spawnsannounced"] = () => World.SpawnedEnemies.AnnouncedCount,
                ["spawnsbuilt"] = () => World.SpawnedEnemies.BuiltCount,
                ["spawnskipped"] = () => World.FogletMinions.Skipped,
                ["conjurecast"] = () => World.FogletMinions.Cast,
                ["traplaid"] = () => World.TrapShare.Laid,
                ["summonsbuilt"] = () => World.LeaseSummons.Built,
                ["thundersent"] = () => World.WeatherSync.ThunderSent,
                ["guardwitness"] = () => World.GuardReach.Witnessed,
                ["volumeentered"] = () => World.GuardReach.Entered,
                ["thundershown"] = () => World.WeatherSync.ThunderShown,
                ["pilesremoved"] = () => World.HaulShare.PilesRemoved,
                ["pileslate"] = () => World.HaulShare.LateSettled,
                ["regionstaken"] = () => World.RegionShare.Taken,
                ["arenajoined"] = () => World.ArenaTeammates.Joined,
                ["arenako"] = () => World.ArenaTeammates.KnockedOut,
                ["summonsbound"] = () => World.LeaseSummons.Bound,
                ["trapshown"] = () => World.TrapShare.Shown,
                ["trapwithdrawn"] = () => World.TrapShare.Withdrawn,
                ["gatesapplied"] = () => World.ArenaGates.Applied,
                ["leasemirrored"] = () => World.LeaseMirror.Mirrored,
                ["arenarefunded"] = () => World.Arena.Refunded,
                ["copyeffects"] = () => World.StatusWatch.CopyEffectsRefused,
            };
            TestAgent.RegisterCommand("counter", name => counters.TryGetValue(name.Trim().ToLowerInvariant(), out Func<int>? read)
                ? read().ToString()
                : throw new InvalidOperationException("counters: " + string.Join(", ", counters.Keys)));
            TestAgent.RegisterState(() => $"session={CoopSession.State} code={CoopSession.SessionCode ?? "-"} " +
                $"players={CoopSession.Players.Count} ff={(CoopSession.FriendlyFire ? "on" : "off")}");
        }

        /// <inheritdoc/>
        public override void OnUpdate()
        {
            base.OnUpdate();

            // Logged so a long frame that expired this mod's timers can be spotted.
            float frame = Time.unscaledDeltaTime;

            if (frame > LongFrameSeconds)
            {
                LongFrames++;
                if (frame > LongestFrame) LongestFrame = frame;

                MelonLogger.Warning($"[Coop] a frame here took {frame:0.0}s. Anything this mod was " +
                    "waiting for has been told that nothing happened, because nothing was observed.");
            }

            // The session tick, the window key and the pause policy run in the main menu too.
            CoopSession.Tick(Time.unscaledDeltaTime);

            if (_windowAction != null && _windowAction.WasReleasedThisFrame())
            {
                LoggerInstance.Msg("[Coop] " + UI.SessionWindow.Toggle());
            }

            World.PausePolicy.Tick(Time.unscaledDeltaTime);

            // The world is forgotten once when it is left, and once when a session ends inside it.
            bool live = CoopSession.IsLive;
            if (!InGameplay)
            {
                if (!_worldForgotten) ForgetWorld(keepHaulBindings: false);
                _worldForgotten = true;
                _wasLive = live;
                return;
            }

            _worldForgotten = false;
            if (_wasLive && !live) ForgetWorld(keepHaulBindings: true);
            _wasLive = live;

            UpdateWorld();
        }

        private static bool _worldForgotten;
        private static bool _wasLive;

        /// <summary>Clears everything this mod holds about the world it was in.</summary>
        private static void ForgetWorld(bool keepHaulBindings)
        {
            {
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

                // Holds the ended world's pooled prefabs. Rebuilt lazily.
                World.ActionPrefabIndex.Clear();

                World.EnemyPinning.Clear();
                World.EnemyPerception.Reset();
                World.CameraReach.Reset();
                World.CutsceneWatch.Reset();
                World.RemoteCutscene.Reset();
                World.CutscenePorts.Reset();
                World.WeatherSync.Reset();
                World.DaytimeSync.Reset();
                World.MapShare.Reset();
                ResetWorldMemory(keepHaulBindings);
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

                // Hands every driven creature back to its own AI, or it stays inert forever.
                World.RemoteEnemies.Clear();

                // Lifts write-protection, or a creature stays invincible into a solo save.
                World.EnemyRegistry.Clear();
            }
        }

        private static void UpdateWorld()
        {
            // First, so write-protection is in place before anything is hit.
            World.EnemyRegistry.Update(Time.unscaledDeltaTime);

            // Its answers arrive as graveyard entries the registry applies.
            World.HostWorldState.Update(Time.unscaledDeltaTime);

            // Rebuilds bodies, so it runs before anything below reaches for one.
            World.EnemyRebirth.Update(Time.unscaledDeltaTime);

            // Before the drivers below, so a summon named this frame is driven this frame.
            World.SummonWatch.Update(Time.unscaledDeltaTime);

            // So a body announced this frame exists before anything drives it.
            World.SpawnedEnemies.Update(Time.unscaledDeltaTime);

            // Before the broadcast, so a downed player sends the health the downing left.
            World.DownedState.Update(Time.unscaledDeltaTime);
            World.GameOver.Update();
            World.Checkpoint.Update(Time.unscaledDeltaTime);

            // After the downed check and before the broadcast, so neither runs a frame late.
            World.RemoteDamage.Update();

            // After the damage, so a poison ticks from the health its blow left.
            World.RemoteStatus.Update();

            World.RemoteGVar.Update();

            // After the flags, so a cutscene plays over the state that came with it.
            World.RemoteCutscene.Update(Time.unscaledDeltaTime);
            World.CutscenePorts.Update();

            World.WeatherSync.Update(Time.unscaledDeltaTime);

            World.ResourceWork.Update();
            World.LootShare.Update();
            World.WorldObjects.Update();
            World.GuardReach.Update();
            World.AnyPlayerReach.Update();

            // Every frame, since a session can start in the main menu before a savegame exists. Cheap once loaded.
            World.LedgerStore.Load();
            World.Arena.Update(Time.unscaledDeltaTime);
            World.Arena.Tick(Time.unscaledDeltaTime);
            World.Trade.Update();
            World.TeammateMenu.Update();

            // After the swings and variables, so this frame's piles and tallies are settled.
            World.HaulShare.Update();

            // Enforces the cutscene move timeout the game sets and never ticks.
            World.SceneMoveWatch.Update(Time.unscaledDeltaTime);

            // After the variables, so the journal reads the quest values that arrived with it.
            World.JournalShare.Update();

            World.NpcPlacement.Update(Time.unscaledDeltaTime);

            World.DaytimeSync.Update(Time.unscaledDeltaTime);

            World.MapShare.Update();

            World.RewardShare.Update();

            World.ItemGrantShare.Update();

            // After the variable applies that arm it.
            World.GateFollow.Update(Time.unscaledDeltaTime);

            // Before the enemy appliers, so an NPC held for a dialogue stops being driven that frame.
            World.EngageWatch.Update(Time.unscaledDeltaTime);
            World.EnemyEngagements.Update(Time.unscaledDeltaTime);
            World.TeamBrawls.Update(Time.unscaledDeltaTime);

            // Before the damage appliers, so a blow lands where the host says the body is.
            World.RemoteEnemies.Update(Time.unscaledDeltaTime);

            // Health before death, so a death is not undone by a figure arriving with it.
            World.RemoteEnemyHealth.Update();

            // Before the reported deaths, so the killer's payout settles first.
            World.ExperienceShare.Update();

            World.RemoteEnemyDeath.Update(Time.unscaledDeltaTime);

            // After this frame's blows are applied, so the health sent includes them.
            World.EnemyHealthBroadcaster.Update(Time.unscaledDeltaTime);
            World.EnemyBroadcaster.Update(Time.unscaledDeltaTime);
            World.LeaseMirror.Update(Time.unscaledDeltaTime);
            World.TrapShare.Update();
            World.LeaseSummons.Update();
            World.RegionShare.Update(Time.unscaledDeltaTime);
            World.ArenaTeammates.Update();
            World.ArenaGates.Update(Time.unscaledDeltaTime);

            World.EnemyPinning.Update(Time.unscaledDeltaTime);

            World.RemoteKnowledge.Update();

            World.LocalPlayerBroadcaster.Update(Time.unscaledDeltaTime);
            World.EquipmentBroadcaster.Update(Time.unscaledDeltaTime);
            World.TalentBroadcaster.Update(Time.unscaledDeltaTime);
            World.RemotePlayers.Update(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Binds the configured key to the session window. An unparsable path leaves the key off.
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

            // Before the session goes, so a mod asking during shutdown sees no half-torn-down state.
            CoopProvider.Unregister();

            CoopSession.Shutdown();
        }
    }
}
