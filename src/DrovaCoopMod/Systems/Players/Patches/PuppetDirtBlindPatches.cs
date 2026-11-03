using Il2CppDrova;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>Harmony entry points of <see cref="PuppetDirtBlind"/>.</summary>
    internal static class PuppetDirtBlindPatches
    {
        internal static bool OnHitPrefix(Entity actor)
            => PuppetDirtBlind.OnHitPrefix(actor);
    }
}
