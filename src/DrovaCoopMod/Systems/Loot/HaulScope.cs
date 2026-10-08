using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.Loot
{
    /// <summary>
    /// Guards a replicated haul. Not <see cref="World.WorkScope"/>, because <c>GVarDerived</c> would then mark the
    /// haul's cascade as derived on the far machine only.
    /// </summary>
    internal readonly struct HaulScope : IApplyScope;
}
