namespace DrovaCoopMod.Systems.World.Patches
{
    /// <summary>The Harmony entry points of <see cref="StoryEffectGate"/>. Each forwards to the system.</summary>
    internal static class StoryEffectGatePatches
    {
        internal static bool SkipWhileSuppressed()
            => StoryEffectGate.SkipWhileSuppressed();
    }
}
