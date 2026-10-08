using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Guards a host world-time write, which <c>ClockGuard</c> would otherwise refuse on a client.
    /// </summary>
    internal readonly struct DaytimeScope : IApplyScope;
}
