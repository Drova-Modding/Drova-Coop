using Drova_Modding_API.Access;
using Drova_Modding_API.GlobalFields;
using DrovaCoopMod.Access;
using DrovaCoopMod.Systems;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(DrovaCoopMod.Core), "Drova Coop", DrovaCoopMod.BuildInfo.Version, "Drova Modding")]
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
        /// <summary>Above this, a frame is long enough to have broken something that counts frames.</summary>
        private const float LongFrameSeconds = 1f;

        /// <summary>
        /// Every co-op system. Each frame updates them top to bottom, and forgetting the world goes bottom to top,
        /// so what a frame drives first is let go last.
        /// </summary>
        private static readonly ICoopSystem[] Systems =
        [
            // First, so write-protection is in place before anything is hit. Forgotten last, since that lifts it.
            new EnemyRegistry(),

            // Its answers arrive as graveyard entries the registry applies.
            new HostWorldState(),

            // Rebuilds bodies, so it runs before anything below reaches for one.
            new EnemyRebirth(),

            // Before the drivers below, so a summon named this frame is driven this frame.
            new SummonWatch(),

            // Hands spawner waves to SpawnedEnemies, which shares them in the same frame.
            new SpawnerShare(),

            // So a body announced this frame exists before anything drives it.
            new SpawnedEnemies(),

            // Before the broadcast, so a downed player sends the health the downing left.
            new DownedState(),
            new GameOver(),
            new Checkpoint(),

            // After the downed check and before the broadcast, so neither runs a frame late.
            new RemoteDamage(),

            // After the damage, so a poison ticks from the health its blow left.
            new RemoteStatus(),

            // Patched before HaulShare, whose prefix on the same GInt.Operate must run after this one's.
            new GVarDerived(),
            new RemoteGVar(),

            // After the flags, so a cutscene plays over the state that came with it.
            new RemoteCutscene(),
            new CutscenePorts(),

            new WeatherSync(),
            new ResourceWork(),
            new LootShare(),
            new WorldObjects(),
            new GuardReach(),
            new AnyPlayerReach(),
            new LedgerStore(),

            // Before the arena, so the arena is forgotten first: its refund counts the bag before an open trade is undone.
            new Trade(),
            new Arena(),
            new TeammateMenu(),

            // After the swings and variables, so this frame's piles and tallies are settled.
            new HaulShare(),

            // Enforces the cutscene move timeout the game sets and never ticks.
            new SceneMoveWatch(),

            // After the variables, so the journal reads the quest values that arrived with it.
            new JournalShare(),
            new NpcPlacement(),
            new DaytimeSync(),
            new MapShare(),
            new RewardShare(),
            new ItemGrantShare(),

            // After the variable applies that arm it.
            new GateFollow(),

            // Before the enemy appliers, so an NPC held for a dialogue stops being driven that frame.
            new EngageWatch(),
            new EnemyEngagements(),
            new TeamBrawls(),

            // Before the damage appliers, so a blow lands where the host says the body is.
            new RemoteEnemies(),

            // Health before death, so a death is not undone by a figure arriving with it.
            new RemoteEnemyHealth(),

            // Before the reported deaths, so the killer's payout settles first.
            new ExperienceShare(),
            new RemoteEnemyDeath(),

            // After this frame's blows are applied, so the health sent includes them.
            new EnemyHealthBroadcaster(),
            new EnemyBroadcaster(),
            new LeaseMirror(),
            new TrapShare(),
            new LeaseSummons(),
            new RegionShare(),
            new ArenaTeammates(),
            new ArenaGates(),
            new EnemyPinning(),
            new RemoteKnowledge(),
            new LocalPlayerBroadcaster(),
            new EquipmentBroadcaster(),
            new TalentBroadcaster(),
            new MorphBroadcaster(),
            new RemotePlayers(),

            // No per-frame work. After TeammateMenu and AnyPlayerReach, whose patches on the same methods run first.
            new PausePolicy(),
            new DialogueWindowGuard(),
            new DamageWatch(),
            new GVarWatch(),
            new StoryEffectGate(),
            new MineDayLoop(),
            new ClockGuard(),
            new EnemyScaling(),
            new SaveBackups(),
            new SpokenLines(),
            new FogletShare(),
            new MapConsumeShare(),
            new FogletMinions(),
            new StorySceneLease(),
            new BlockVerdicts(),
            new CacheShare(),
            new CutsceneWatch(),
            new ItemUseWatch(),
            new StatusWatch(),
            new KnowledgeWatch(),
            new PuppetCrits(),
            new PuppetPoise(),
            new PuppetCombatState(),
            new DownedTargeting(),
            new EnemyPerception(),
            new CameraReach(),
            new EnemyCognition(),
            new EnemyThreat(),
            new PuppetTargets(),
            new RemoteEnemyBlows(),
            new PuppetBrawlGate(),
            new NameTags(),
            new ActionPrefabIndex(),
            new EnemyDeathWatch(),
            new Invites(),
            new HostSettings(),
        ];

        private static bool _worldForgotten;
        private static bool _wasLive;

        private bool _validatedDeclarations;

        /// <summary>
        /// Whether a world is up rather than the main menu.
        /// </summary>
        internal static bool InGameplay { get; private set; }

        /// <summary>
        /// From a savegame's loaded event until its world is ready. The old world's bodies outlive the event by a
        /// second, so what they say about life and death must not be told to the session.
        /// </summary>
        internal static bool WorldLoading { get; private set; }

        // The old world reads ready until the load clears it, so ready counts only after it was seen unready.
        private static bool _sawUnready;
        private static float _loadingFor;

        // A load whose ready flag never drops ends the loading window after this long. A load slower than this ends it early.
        private const float MaxLoadSeconds = 60f;

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

            CoopSession.Initialize(Systems);
            CoopCommands.Register();

            PauseMenuAccess.AddButton("coop_checkpoint",
                () => "Back to party checkpoint",
                () => MelonLogger.Msg("[Coop] " + GameOver.BackToCheckpoint()),
                visible: () => CoopSession.HostsWorld && Checkpoint.AllowsGoingBack,
                interactable: () => Checkpoint.Committed != 0);

            // Early, since other mods query the API at their own init.
            CoopProvider.Register();

#if DEBUG
            TestCommands.Register();
#endif

            foreach (ICoopSystem system in Systems)
            {
                string? hookSwitch = system.HookSwitch;
                if (hookSwitch == null || HookSwitches.IsOn(hookSwitch)) system.Initialize(HarmonyInstance);
            }

            // Loading a savegame inside gameplay rewinds this world without ending the session, so it must resync.
            Drova_Modding_API.Systems.SaveGame.SaveGameSystem.AfterSaveGameLoaded += OnSaveGameLoaded;
            Drova_Modding_API.Systems.SaveGame.SaveGameSystem.BeforeSaveGameLoaded += _ =>
            {
                RemoteGVar.HostLoadStarted();

                // Refunds and trade undos write inventories, so they run on the world being left, not the loaded one.
                Arena.Reset();
                Trade.Reset();
            };

            UI.WindowKey.Initialize();
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
                    GVarWatch.ValidateDeclarations();
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
        private static void OnSaveGameLoaded(Il2CppDrova.Saveables.Savegame? saveGame)
        {
            // First, so a catch-up a client asked for meanwhile is read from the loaded world.
            RemoteGVar.HostLoadFinished();
            HostSettings.SaveLoaded();

            // Fired before the old world's bodies are gone, and the forgetting below makes their deaths look new.
            WorldLoading = true;
            _sawUnready = false;
            _loadingFor = 0f;

            // Read and cleared on every load, or a client's or a solo load leaves it set for a later session.
            bool partyLoad = GameOver.PartyLoading;
            GameOver.PartyLoading = false;

            if (CoopSession.HostsWorld)
            {
                // Otherwise the host's memory would swallow re-setting a flag it set before the load.
                // Teammates keep their progress (RemoteGVar).
                ForgetWorld(loaded: true, keepHaulBindings: true);
                if (partyLoad) return;

                // A save of a party checkpoint takes everyone back with the host, each to their own copy. In every mode:
                // the alternative is two worlds drifting apart.
                int loaded = saveGame?.MetaInfo?.GetIdentifier().SlotIndex ?? -1;
                if (Checkpoint.TryGetRound(loaded, out ushort round))
                {
                    MelonLogger.Msg($"[Coop] the host went back to a save of checkpoint {round}; everyone goes back to it.");
                    GameOver.ReportGameOver(round);
                    return;
                }

                // No teammate holds this point, and lowering their progress cannot be undone, so the session ends instead.
                CoopSession.FailWith("You loaded a save from before the party's last checkpoint, and your teammates are " +
                    "ahead of it. Host again from this save, and have your teammates load a save from the same point.");
                return;
            }

            if (!CoopSession.JoinedWorld) return;

            // Haul bindings are authored facts, not session state, so they survive.
            ForgetWorld(loaded: true, keepHaulBindings: true);

            MelonLogger.Msg("[Coop] a savegame was loaded inside the session; this world has gone " +
                "backwards. Asking the host for its variables and journal again.");

            RemoteGVar.RequestResync(Messages.GVarResyncRequestMessage.ReasonSavegameLoaded, false);
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

            if (UI.WindowKey.Pressed)
            {
                LoggerInstance.Msg("[Coop] " + UI.SessionWindow.Toggle());
            }

            PausePolicy.Tick();
            TrackWorldLoading();

            // The world is forgotten once when it is left, and once when a session ends inside it.
            bool live = CoopSession.IsLive;
            if (!InGameplay)
            {
                if (!_worldForgotten) ForgetWorld(loaded: false, keepHaulBindings: false);
                _worldForgotten = true;
                _wasLive = live;
                return;
            }

            _worldForgotten = false;
            if (_wasLive && !live) ForgetWorld(loaded: false, keepHaulBindings: true);
            _wasLive = live;

            foreach (ICoopSystem system in Systems)
            {
                system.Update(Time.unscaledDeltaTime);
            }
        }

        private static void TrackWorldLoading()
        {
            if (!WorldLoading) return;

            _loadingFor += Time.unscaledDeltaTime;
            if (_loadingFor > MaxLoadSeconds)
            {
                WorldLoading = false;
                return;
            }

            try
            {
                bool ready = !Il2CppDrova.SceneGameHandler.IsLoadingScreenActive
                    && Il2CppDrova.LoadingScreenHandles.LoadingScreenHandler.Instance?.IsWorldReady() == true;
                if (!ready) _sawUnready = true;
                else if (_sawUnready) WorldLoading = false;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Coop] reading the loading screen failed: " + e.Message);
            }
        }

        /// <summary>Clears what this mod holds about the world it was in, or only what a savegame load rewinds.</summary>
        private static void ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            for (int index = Systems.Length - 1; index >= 0; index--)
            {
                Systems[index].ForgetWorld(loaded, keepHaulBindings);
            }

            if (loaded) return;

            LongFrames = 0;
            LongestFrame = 0f;
        }

        /// <inheritdoc/>
        public override void OnGUI()
        {
            base.OnGUI();
            NameTags.Draw();
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
