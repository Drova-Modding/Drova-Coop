using Il2CppDrova;
using Il2CppDrova.Crafting;
using Il2CppDrova.Items;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="KnowledgeWatch"/>. Each forwards to the system.</summary>
    internal static class KnowledgeWatchPatches
    {
        internal static void AddRecipePostfix(RecipeRepository __instance, IRecipe recipe)
            => KnowledgeWatch.AddRecipePostfix(__instance, recipe);

        internal static void LetterPickedUpPostfix(ItemBhvr_Letter __instance, Actor actor, bool bySaveSystem)
            => KnowledgeWatch.LetterPickedUpPostfix(__instance, actor, bySaveSystem);
    }
}
