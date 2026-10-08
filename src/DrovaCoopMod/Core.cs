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
using UnityEngine.InputSystem;

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
            new FogletMinions(),
            new StorySceneLease(),
            new BlockVerdicts(),
            new CacheShare(),
            new CutsceneWatch(),
            new ItemUseWatch(),
            new StatusWatch(),
            new KnowledgeWatch(),
            new PuppetCrits(),
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

        private InputAction? _windowAction;
        private bool _validatedDeclarations;

        /// <summary>
        /// Whether a world is up rather than the main menu.
        /// </summary>
        internal static bool InGameplay { get; private set; }

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

            // The host's way back to the party checkpoint, beside the game's own "Load latest".
            PauseMenuAccess.AddButton("coop_checkpoint",
                () => "Back to party checkpoint",
                () => MelonLogger.Msg("[Coop] " + GameOver.BackToCheckpoint()),
                visible: () => CoopSession.State == CoopSessionState.Hosting && Checkpoint.AllowsGoingBack,
                interactable: () => Checkpoint.Committed != 0);

            // Early, since other mods query the API at their own init.
            CoopProvider.Register();

            TestCommands.Register();

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

            // Read and cleared on every load, or a client's or a solo load leaves it set for a later session.
            bool partyLoad = GameOver.PartyLoading;
            GameOver.PartyLoading = false;

            if (CoopSession.State == CoopSessionState.Hosting)
            {
                // Otherwise the host's memory would swallow re-setting a flag it set before the load.
                // Teammates keep their progress (RemoteGVar).
                ForgetWorld(loaded: true, keepHaulBindings: true);
                if (partyLoad) return;

                // A save of a party checkpoint takes everyone back with the host, each to their own copy.
                int loaded = saveGame?.MetaInfo?.GetIdentifier().SlotIndex ?? -1;
                if (Checkpoint.AllowsGoingBack && Checkpoint.TryGetRound(loaded, out ushort round))
                {
                    MelonLogger.Msg($"[Coop] the host went back to a save of checkpoint {round}; everyone goes back to it.");
                    GameOver.ReportGameOver(round);
                    return;
                }

                MelonLogger.Warning("[Coop] the host loaded a savegame inside the session that no party checkpoint stands " +
                    "for (made before the session, or its round never completed). Teammates keep their progress; this " +
                    "world is now behind them. Restarting the session from this save is the clean way back to one world.");

                return;
            }

            if (CoopSession.State != CoopSessionState.Joined) return;

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

            if (_windowAction != null && _windowAction.WasReleasedThisFrame())
            {
                LoggerInstance.Msg("[Coop] " + UI.SessionWindow.Toggle());
            }

            PausePolicy.Tick();

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
            NameTags.Draw();

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
