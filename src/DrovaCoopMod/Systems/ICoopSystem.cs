using DrovaCoopMod.Messages;

namespace DrovaCoopMod.Systems
{
    /// <summary>
    /// One co-op system's part in the mod's lifecycle. <see cref="Core"/> holds the systems in one ordered list and
    /// calls each member on every system in that order, except <see cref="ForgetWorld"/>, which runs in reverse.
    /// </summary>
    internal interface ICoopSystem
    {
        /// <summary>The <see cref="HookSwitches"/> name that keeps <see cref="Initialize"/> from running. Null: it always runs.</summary>
        string? HookSwitch => GetType().Name;

        /// <summary>Applies the system's patches. Once, at mod start.</summary>
        void Initialize(HarmonyLib.Harmony harmony) { }

        /// <summary>Registers the messages the system takes. Once, before any session can start.</summary>
        void RegisterMessages() { }

        /// <summary>Runs once per frame while a world is up.</summary>
        void Update(float unscaledDeltaTime) { }

        /// <summary>A player left the session.</summary>
        void PlayerLeft(PlayerId player) { }

        /// <summary>
        /// Forgets what the system holds about the world. Runs in reverse list order, so what the frame drives first is let go last.
        /// </summary>
        /// <param name="loaded">A savegame was loaded inside the same world: forget only what the load rewinds.</param>
        /// <param name="keepHaulBindings">Whether to keep which variable counts which rock face's piles.</param>
        void ForgetWorld(bool loaded, bool keepHaulBindings) { }

        /// <summary>The session ended: drop what it left queued.</summary>
        void EndSession() { }
    }
}
