using Il2Cpp;
using Il2CppDrova;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="PuppetCrits"/>. Each forwards to the system.</summary>
    internal static class PuppetCritsPatches
    {
        internal static void CalcCritDataPostfix(Entity source, ref CritData __result)
            => PuppetCrits.CalcCritDataPostfix(source, ref __result);
    }
}
