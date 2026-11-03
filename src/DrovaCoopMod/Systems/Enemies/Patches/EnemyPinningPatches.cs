using Il2CppDrova;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>Harmony entry points of <see cref="EnemyPinning"/>.</summary>
    internal static class EnemyPinningPatches
    {
        /// <summary>The parameter keeps the game's own name, arg0, since Harmony binds by name.</summary>
        internal static bool ActorOutOfVisionPrefix(Actor arg0)
            => EnemyPinning.ActorOutOfVisionPrefix(arg0);
    }
}
