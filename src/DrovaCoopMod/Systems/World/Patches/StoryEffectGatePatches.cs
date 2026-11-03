namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>Harmony entry points of <see cref="StoryEffectGate"/>.</summary>
    internal static class StoryEffectGatePatches
    {
        internal static bool SkipWhileSuppressed()
            => StoryEffectGate.SkipWhileSuppressed();
    }
}
