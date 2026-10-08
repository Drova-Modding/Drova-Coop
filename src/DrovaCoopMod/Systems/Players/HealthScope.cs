using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>Guards replicated health writes.</summary>
    internal readonly struct HealthScope : IApplyScope;
}
