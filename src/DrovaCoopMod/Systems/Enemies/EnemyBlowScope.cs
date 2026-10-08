using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Guards a relayed blow dealt through the game's damage path. Unlike a replicated death, the death a
    /// blow causes here is this machine's own news to announce.
    /// </summary>
    internal readonly struct EnemyBlowScope : IApplyScope;
}
