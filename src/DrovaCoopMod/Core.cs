using Drova_Modding_API.Access;
using Drova_Modding_API.GlobalFields;
using DrovaCoopMod.Session;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(DrovaCoopMod.Core), "Drova Coop", DrovaCoopMod.BuildInfo.Version, "Drova Modding", null)]
[assembly: MelonGame("Just2D", "Drova")]
[assembly: MelonAdditionalDependencies("Drova Modding API")]

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

        /// <inheritdoc/>
        public override void OnInitializeMelon()
        {
            base.OnInitializeMelon();

            CoopPreferences.Initialize();
            CoopSession.Initialize();

            _hostAction.Enable();
            _joinAction.Enable();
            _leaveAction.Enable();

            LoggerInstance.Msg(NetworkAccess.IsSupported
                ? "Drova Coop ready. F7 hosts, F8 joins, F9 leaves."
                : "Drova Coop loaded, but this Drova Modding API build has no coop support.");
        }

        /// <inheritdoc/>
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            base.OnSceneWasLoaded(buildIndex, sceneName);

            // A session is only meaningful with a world loaded; the main menu has nothing to sync.
            _inGameplay = sceneName == SceneNames.GameplayMain;

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

            if (!_inGameplay) return;

            if (_hostAction.WasReleasedThisFrame())
            {
                CoopSession.HostViaRelay(
                    CoopPreferences.RelayAddress,
                    CoopPreferences.RelayPort,
                    CoopPreferences.SessionCodeOrNull(),
                    CoopPreferences.PasswordOrNull());
            }

            if (_joinAction.WasReleasedThisFrame())
            {
                string? code = CoopPreferences.SessionCodeOrNull();
                if (code == null)
                {
                    LoggerInstance.Warning("Set DrovaCoop.SessionCode to the host's code before joining.");
                }
                else
                {
                    CoopSession.JoinViaRelay(CoopPreferences.RelayAddress, CoopPreferences.RelayPort, code, CoopPreferences.PasswordOrNull());
                }
            }

            if (_leaveAction.WasReleasedThisFrame())
            {
                CoopSession.Leave();
            }
        }

        /// <inheritdoc/>
        public override void OnDeinitializeMelon()
        {
            base.OnDeinitializeMelon();
            CoopSession.Shutdown();
        }
    }
}
