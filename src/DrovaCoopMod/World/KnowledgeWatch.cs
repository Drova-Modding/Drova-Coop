using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.Crafting;
using Il2CppDrova.Items;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Notices when the local player learns something the other players should know too, and says so.
    ///
    /// One world means one copy of the paper. A recipe scrap and a letter are consumed when they are
    /// taken: whoever reaches them first learns them, the item is gone, and there is nothing left in the
    /// world for anybody else to find. Without this, two players exploring together end up with
    /// permanently different recipe books and journals, and no way to fix it short of starting again.
    ///
    /// The two things are watched at different points because the game learns them at different moments.
    /// A recipe is learned when the item is used - so the funnel is the repository that stores it, which
    /// catches the dialogue that teaches one just as well. A letter is learned when it is picked up,
    /// not read, so that is where it is caught.
    /// </summary>
    internal static class KnowledgeWatch
    {
        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(RecipeRepository), "AddRecipe",
                typeof(KnowledgeWatch), nameof(AddRecipePostfix));

            Hooking.TryPostfix(harmony, typeof(ItemBhvr_Letter), "ItemAddedFirstTime",
                typeof(KnowledgeWatch), nameof(LetterPickedUpPostfix));
        }

        /// <summary>
        /// Announces a recipe the local player just learned.
        ///
        /// The repository has no owner to ask, so the one the local player holds is fetched and compared -
        /// which also keeps every NPC's repository out of this, and there are a great many of those.
        /// </summary>
        private static void AddRecipePostfix(RecipeRepository __instance, IRecipe recipe)
        {
            try
            {
                // The recipe this mod itself just taught, arriving back through the funnel it was always
                // going to arrive back through.
                if (RemoteApply<KnowledgeScope>.InProgress) return;

                if (!CoopSession.IsLive) return;
                if (__instance == null || recipe == null) return;
                if (!IsLocalPlayers(__instance)) return;

                string guid = recipe.GUID;
                if (string.IsNullOrEmpty(guid)) return;

                CoopSession.BroadcastKnowledge(KnowledgeMessage.KindRecipe, guid);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Know] watching a learned recipe failed: " + e);
            }
        }

        /// <summary>
        /// Announces a letter the local player just picked up.
        /// </summary>
        /// <param name="__instance">The letter behavior on the item that was picked up.</param>
        /// <param name="actor">Who picked it up. Every NPC inventory reaches this too.</param>
        /// <param name="bySaveSystem">True when the world is being loaded rather than played. Every letter
        /// the player has ever found comes back through here on a load, and announcing those would have
        /// each machine teach the others its whole journal on every save.</param>
        private static void LetterPickedUpPostfix(ItemBhvr_Letter __instance, Actor actor, bool bySaveSystem)
        {
            try
            {
                if (RemoteApply<KnowledgeScope>.InProgress) return;
                if (bySaveSystem) return;

                if (!CoopSession.IsLive) return;
                if (__instance == null || actor == null) return;
                if (!__instance.HasTopic) return;
                if (!RemotePlayers.IsLocalPlayer(actor)) return;

                // The item that carries the letter, not the journal topic. Topics have no database to look
                // them up in on the far side; items do, and the equipment sync resolves them constantly.
                Item item = __instance.RelatedItem;
                if (item == null) return;

                string id = item.ReadableId;
                if (string.IsNullOrEmpty(id)) return;

                CoopSession.BroadcastKnowledge(KnowledgeMessage.KindWriting, id);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Know] watching a picked-up letter failed: " + e);
            }
        }

        private static bool IsLocalPlayers(RecipeRepository repository)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return false;

            ICraftingActorModule crafting = player.CraftingActorModule;
            if (crafting == null) return false;

            IRecipeRepository mine = crafting.GetRecipeRepository();

            // By pointer, for the usual reason: two proxies over one native object are different managed
            // instances, so the equality operator answers a question about destruction rather than about
            // identity.
            return mine != null && mine.Pointer == repository.Pointer;
        }
    }
}
