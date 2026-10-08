using Il2CppDrova.Cognition;
using Il2CppDrova.Utilities.LazyLoading;
using Il2CppGilzoide.UpdateManager.Internal;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>The Harmony entry points of <see cref="EnemyCognition"/>. Each forwards to the system.</summary>
    internal static class EnemyCognitionPatches
    {
        internal static void GetCognitionObjectsPostfix(CognitionOctreeManager __instance, Vector2 position, float radius, FastRemoveList<ICognitionObject> __result)
            => EnemyCognition.GetCognitionObjectsPostfix(__instance, position, radius, __result);
    }
}
