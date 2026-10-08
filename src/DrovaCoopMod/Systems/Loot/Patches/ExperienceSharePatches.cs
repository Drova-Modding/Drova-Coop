using Il2CppDrova;

namespace DrovaCoopMod.Systems.Loot.Patches
{
    /// <summary>The Harmony entry points of <see cref="ExperienceShare"/>. Each forwards to the system.</summary>
    internal static class ExperienceSharePatches
    {
        internal static bool CheckPrefix(Actor owner, int expOnDead)
            => ExperienceShare.CheckPrefix(owner, expOnDead);

        internal static void CheckPostfix()
            => ExperienceShare.CheckPostfix();
    }
}
