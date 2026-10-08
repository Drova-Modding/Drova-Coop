using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Guards a teammate's world-object state written here (<see cref="WorldObjects"/>), whose
    /// listener fires the same for loads and players.
    /// </summary>
    internal readonly struct WorldObjectScope : IApplyScope;
}
