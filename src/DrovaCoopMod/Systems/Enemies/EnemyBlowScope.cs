using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Guards a relayed blow dealt through the game's damage path. Unlike a replicated death, a death the
    /// blow causes here is announced by this machine.
    /// </summary>
    internal readonly struct EnemyBlowScope : IApplyScope;
}
