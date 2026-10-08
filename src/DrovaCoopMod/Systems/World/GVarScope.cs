using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>Guards replicated global-variable and quest-state writes.</summary>
    internal readonly struct GVarScope : IApplyScope;
}
