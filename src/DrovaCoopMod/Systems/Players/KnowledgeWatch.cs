using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Players.Patches;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.Crafting;
using Il2CppDrova.Items;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Shares recipes and letters the local player learns, since the item that teaches them exists only once.
    /// Recipes are caught at <c>RecipeRepository.AddRecipe</c> (covers dialogue too), letters on pickup.
    /// </summary>
    internal sealed class KnowledgeWatch : ICoopSystem
    {
        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(RecipeRepository), "AddRecipe",
                typeof(KnowledgeWatchPatches), nameof(KnowledgeWatchPatches.AddRecipePostfix));

            Hooking.TryPostfix(harmony, typeof(ItemBhvr_Letter), "ItemAddedFirstTime",
                typeof(KnowledgeWatchPatches), nameof(KnowledgeWatchPatches.LetterPickedUpPostfix));
        }

        /// <summary>
        /// Announces a recipe the local player just learned. The repository has no owner, so it is compared to the player's.
        /// </summary>
        internal static void AddRecipePostfix(RecipeRepository __instance, IRecipe? recipe)
        {
            try
            {
                if (RemoteApply<KnowledgeScope>.InProgress) return;

                if (!CoopSession.SharesWorld) return;
                if (recipe == null) return;
                if (!IsLocalPlayers(__instance)) return;

                string guid = recipe.GUID;
                if (string.IsNullOrEmpty(guid)) return;

                RemoteKnowledge.BroadcastKnowledge(KnowledgeMessage.KindRecipe, guid);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Know] watching a learned recipe failed: " + e);
            }
        }

        /// <summary>
        /// Announces a letter the local player just picked up. Save loads replay every letter, so those are skipped.
        /// </summary>
        internal static void LetterPickedUpPostfix(ItemBhvr_Letter __instance, Actor actor, bool bySaveSystem)
        {
            try
            {
                if (RemoteApply<KnowledgeScope>.InProgress) return;
                if (bySaveSystem) return;

                if (!CoopSession.SharesWorld) return;
                if (__instance == null || actor == null) return;
                if (!__instance.HasTopic) return;
                if (!RemotePlayers.IsLocalPlayer(actor)) return;

                // The item, not the topic: topics have no database to resolve them on the far side.
                Item? item = __instance.RelatedItem;
                if (item == null) return;

                string id = item.ReadableId;
                if (string.IsNullOrEmpty(id)) return;

                RemoteKnowledge.BroadcastKnowledge(KnowledgeMessage.KindWriting, id);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Know] watching a picked-up letter failed: " + e);
            }
        }

        private static bool IsLocalPlayers(RecipeRepository repository)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return false;

            ICraftingActorModule? crafting = player.CraftingActorModule;
            if (crafting == null) return false;

            IRecipeRepository? mine = crafting.GetRecipeRepository();

            return mine != null && mine.Pointer == repository.Pointer;
        }
    }
}
