using DrovaCoopMod.Sync;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>Guards replicated journal entries.</summary>
    internal readonly struct JournalScope : IApplyScope;
}
