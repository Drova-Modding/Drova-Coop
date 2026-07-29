using Drova_Modding_API.Access;
using Drova_Modding_API.GlobalFields;
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
    /// layer its lifecycle, and offer hotkeys until there is a real UI. Everything that talks to the wire
    /// lives in <see cref="CoopSession"/>, and everything below that belongs to the Drova Modding API.
    /// </summary>
    public class Core : MelonMod
    {
        private readonly InputAction _hostAction = new("CoopHost", InputActionType.Button, "<Keyboard>/f7");
        private readonly InputAction _joinAction = new("CoopJoin", InputActionType.Button, "<Keyboard>/f8");
        private readonly InputAction _leaveAction = new("CoopLeave", InputActionType.Button, "<Keyboard>/f9");
        private bool _inGameplay;

#if DEBUG
        private readonly InputAction _probeReportAction = new("CoopProbeReport", InputActionType.Button, "<Keyboard>/f10");
        // Third key, because the first two were taken: F11 by DrovaTimeSpeed, F6 by the gvar editor. The
        // InputSystem names keys by physical position using the US layout, so "backslash" is the key left
        // of Enter - '#' on a German keyboard. Guessing free keys is not a strategy; a cheat command via
        // CheatMenuAccess cannot collide with anything and is the real fix when this stops being a spike.
        private readonly InputAction _puppetAction = new("CoopPuppet", InputActionType.Button, "<Keyboard>/backslash");
        private bool _probed;
#endif

        /// <inheritdoc/>
        public override void OnInitializeMelon()
        {
            base.OnInitializeMelon();

            CoopPreferences.Initialize();
            CoopSession.Initialize();
            CoopCommands.Register();

            // Only subscribes to the option window's open event; everything it does reaches for the game
            // from inside that event, by which point there is a game to reach for.
            UI.CoopOptions.Initialize();

            // Patching is safe here because it resolves types, which exist as soon as the assemblies are
            // loaded. Anything reaching for a game manager is not, and is deferred to the first scene.
            World.PausePolicy.Initialize(HarmonyInstance);
            World.DamageWatch.Initialize(HarmonyInstance);
            World.ItemUseWatch.Initialize(HarmonyInstance);
            World.StatusWatch.Initialize(HarmonyInstance);
            World.KnowledgeWatch.Initialize(HarmonyInstance);
            World.PuppetCrits.Initialize(HarmonyInstance);
            World.DownedTargeting.Initialize(HarmonyInstance);

            _hostAction.Enable();
            _joinAction.Enable();
            _leaveAction.Enable();

#if DEBUG
            _probeReportAction.Enable();
            _puppetAction.Enable();
#endif

            LoggerInstance.Msg(NetworkAccess.IsSupported
                ? "Drova Coop ready. F7 hosts, F8 joins, F9 leaves."
                : "Drova Coop loaded, but this Drova Modding API build has no coop support.");

#if DEBUG
            LoggerInstance.Msg("Drova Coop diagnostics: '#' toggles the puppet, F10 reports probe hit counts.");
#endif
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
            // false. That silently disabled every hotkey and, worse, made the teardown below fire on the
            // first streamed chunk: a live session would drop the moment the player walked somewhere.
            if (sceneName == SceneNames.GameplayMain)
            {
                _inGameplay = true;
            }
            else if (sceneName == SceneNames.MainMenu)
            {
                _inGameplay = false;
            }

#if DEBUG
            // From a loaded world, not melon init: a closed generic's native MethodInfo is only reliably
            // resolvable once the game has instantiated that generic at least once.
            if (_inGameplay && !_probed)
            {
                _probed = true;

                List<Diagnostics.ProbeTargets.Target> targets = Diagnostics.ProbeTargets.Resolve();
                Diagnostics.InlineProbe.Run(targets);
                Diagnostics.PatchProbe.Apply(HarmonyInstance, targets);
            }
#endif

            if (!_inGameplay && CoopSession.State != CoopSessionState.Idle)
            {
                CoopSession.Leave();
            }
        }

        /// <inheritdoc/>
        public override void OnUpdate()
        {
            base.OnUpdate();

            // Ahead of the gameplay gate on purpose: a join already in flight has a deadline to meet, and
            // unscaled time so that an open menu does not stop the clock the transport is still running on.
            CoopSession.Tick(Time.unscaledDeltaTime);

            // Ahead of the gameplay gate too: a world left frozen is exactly the state in which nothing
            // else runs, so the switch that unfreezes it cannot sit behind another condition.
            World.PausePolicy.Tick(Time.unscaledDeltaTime);

#if DEBUG
            if (_probeReportAction.WasReleasedThisFrame())
            {
                Diagnostics.PatchProbe.Report();
            }
#endif

            if (!_inGameplay)
            {
                // The bodies belong to the world that just went away; holding references to destroyed
                // objects across a scene change is how a mod starts logging null reference exceptions
                // from somewhere unrelated.
                World.RemotePlayers.Clear();
                World.NameTags.Reset();
                World.EquipmentBroadcaster.Reset();
                World.ItemUseWatch.Reset();
                return;
            }

            // Ahead of the broadcast, so a player put on the floor this frame sends the health that
            // decision left them on rather than the figure that would have killed them.
            World.DownedState.Update(Time.unscaledDeltaTime);

            // Between the two, deliberately. Applying a blow after the downed check would let a player be
            // knocked down a frame late, and applying it after the broadcast would send the health they
            // had before the hit - so the far end sees its own swing land one tick later than it did.
            World.RemoteDamage.Update();

            // After the damage, so a poison that arrives alongside the blow that delivered it starts
            // ticking from the health that blow left rather than from the health before it.
            World.RemoteStatus.Update();

            // Order does not matter here the way it does above - nothing else reads a recipe book - so it
            // sits with the other appliers rather than anywhere in particular.
            World.RemoteKnowledge.Update();

            World.LocalPlayerBroadcaster.Update(Time.unscaledDeltaTime);
            World.EquipmentBroadcaster.Update(Time.unscaledDeltaTime);
            World.RemotePlayers.Update(Time.unscaledDeltaTime);

#if DEBUG
            if (_puppetAction.WasReleasedThisFrame())
            {
                Diagnostics.PuppetSpike.Toggle();
            }
#endif

            if (_hostAction.WasReleasedThisFrame())
            {
                LoggerInstance.Msg(CoopActions.Host());
            }

            if (_joinAction.WasReleasedThisFrame())
            {
                LoggerInstance.Msg(CoopActions.Join());
            }

            if (_leaveAction.WasReleasedThisFrame())
            {
                LoggerInstance.Msg(CoopActions.Leave());
            }
        }

        /// <inheritdoc/>
        public override void OnGUI()
        {
            base.OnGUI();
            World.NameTags.Draw();
        }

        /// <inheritdoc/>
        public override void OnDeinitializeMelon()
        {
            base.OnDeinitializeMelon();
            CoopSession.Shutdown();
        }
    }
}
