using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Guards replicated work at a resource spot. Separate from <see cref="GVarScope"/> because a replayed
    /// swing legitimately writes gvars such as the rubble counter.
    /// </summary>
    internal readonly struct WorkScope : IApplyScope;
}
