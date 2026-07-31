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

            // Before anything else has a chance to ask. Other mods query the API at their own init, and
            // one that asks before this registers is told it is playing alone - which for a mod that
            // spawns things is the wrong answer in the one direction that matters.
            Session.CoopProvider.Register();

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
            World.EnemyPerception.Initialize(HarmonyInstance);
            World.EnemyCognition.Initialize(HarmonyInstance);
            World.EnemyThreat.Initialize(HarmonyInstance);
            World.RemoteEnemyBlows.Initialize(HarmonyInstance);
            World.HostWorldState.Initialize(HarmonyInstance);
            World.EnemyRebirth.Initialize(HarmonyInstance);
            World.ExperienceShare.Initialize(HarmonyInstance);
            World.SummonWatch.Initialize(HarmonyInstance);
            World.SpawnedEnemies.Initialize();

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
                Diagnostics.EnemyProbe.Report();
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
                World.RemoteEnemyDeath.Clear();
                World.RemoteEnemyHealth.Clear();
                World.EnemyHealthBroadcaster.Clear();
                World.EnemyBroadcaster.Clear();

                // Lets go of every creature the host was holding loaded. Pinning without unpinning is a
                // host whose memory grows for as long as it runs.
                World.EnemyPinning.Clear();
                World.EnemyPerception.Reset();
                World.EnemyCognition.Reset();
                World.EnemyThreat.Reset();
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

            // Between the two, deliberately. Applying a blow after the downed check would let a player be
            // knocked down a frame late, and applying it after the broadcast would send the health they
            // had before the hit - so the far end sees its own swing land one tick later than it did.
            World.RemoteDamage.Update();

            // After the damage, so a poison that arrives alongside the blow that delivered it starts
            // ticking from the health that blow left rather than from the health before it.
            World.RemoteStatus.Update();

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

            // After the registry has decided what the host owns, because that set is what gets held.
            World.EnemyPinning.Update(Time.unscaledDeltaTime);

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

            // Before the session goes, so a mod asking during its own shutdown is told it is alone rather
            // than reading half-torn-down session state.
            Session.CoopProvider.Unregister();

            CoopSession.Shutdown();
        }
    }
}
