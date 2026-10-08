using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Guards replicated health writes to non-players. Separate from <see cref="Players.HealthScope"/> because an
    /// enemy's death can legitimately damage players.
    /// </summary>
    internal readonly struct EnemyHealthScope : IApplyScope;
}
