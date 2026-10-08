using Il2CppDrova;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies.Patches
{
    /// <summary>The Harmony entry points of <see cref="FogletMinions"/>. Each forwards to the system.</summary>
    internal static class FogletMinionsPatches
    {
        internal static bool SpawnPrefix(Il2CppDrova.ABI.ABI_Bhvr_SpawnEntity __instance)
            => FogletMinions.SpawnPrefix(__instance);

        internal static bool ConjurePrefix(GameObject prefab, Entity? owner)
            => FogletMinions.ConjurePrefix(prefab, owner);

        internal static void ConjurePostfix(Il2CppDrova.ABI.ABI_Bhvr_Conjure __instance, GameObject prefab, Entity? owner)
            => FogletMinions.ConjurePostfix(__instance, prefab, owner);
    }
}
