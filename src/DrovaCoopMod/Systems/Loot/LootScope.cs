using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.Loot
{
    /// <summary>
    /// Guards a shared take put into this world's container (<see cref="LootShare"/>), whose refill
    /// fires the take listener.
    /// </summary>
    internal readonly struct LootScope : IApplyScope;
}
