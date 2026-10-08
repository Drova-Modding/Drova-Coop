using Il2CppDrova;
using Il2CppDrova.Saveables;

namespace DrovaCoopMod.Systems.Loot.Patches
{
    /// <summary>The Harmony entry points of <see cref="CacheShare"/>. Each forwards to the system.</summary>
    internal static class CacheSharePatches
    {
        internal static void InitRootPostfix(Saveable_LootTablePickups __instance)
            => CacheShare.InitRootPostfix(__instance);

        internal static void SpawnPostfix(Saveable_LootTablePickups __instance, SpawnFromLootTable.SpawnObjectEventArgs arg0)
            => CacheShare.SpawnPostfix(__instance, arg0);

        internal static void LootedPostfix(Saveable_LootTablePickups __instance)
            => CacheShare.LootedPostfix(__instance);
    }
}
