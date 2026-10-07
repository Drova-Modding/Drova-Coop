using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.Crafting;
using Il2CppDrova.GlobalVarSystem;
using Il2CppInterop.Runtime;
using Il2CppDrova.Items;
using Il2CppDrova.JournalSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Teaches the local player recipes and letters a teammate learned. The far half of <see cref="KnowledgeWatch"/>.
    /// </summary>
    internal static class RemoteKnowledge
    {
        // Caps per-frame work so a peer cannot keep the bounded queue topped up into unbounded work.
        private const int MaxPerFrame = 8;

        // Warnings embed peer-chosen text, so only the first few are logged and the rest counted.
        private const int MaxWarnings = 10;

        private static int _warnings;
        private static int _refused;

        /// <summary>Teaches everything that has arrived. Call once per frame from a loaded world.</summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            for (int taught = 0; taught < MaxPerFrame; taught++)
            {
                if (!CoopSession.TryTakeKnowledge(out KnowledgeMessage message)) return;

                Apply(player, message);
            }
        }

        private static void Apply(Actor player, in KnowledgeMessage message)
        {
            try
            {
                // Keeps KnowledgeWatch from announcing it back.
                using (RemoteApply<KnowledgeScope>.Enter())
                {
                    switch (message.Kind)
                    {
                        case KnowledgeMessage.KindRecipe:
                            LearnRecipe(player, message);
                            break;

                        case KnowledgeMessage.KindWriting:
                            LearnWriting(message);
                            break;

                        // A peer on a later version.
                        default:
                            Refuse($"{message.Learner} learned something of kind {message.Kind}, which this build does not know.");
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Know] teaching something learned elsewhere failed: " + e);
            }
        }

        private static void Refuse(string reason)
        {
            _refused++;

            if (_warnings >= MaxWarnings) return;

            _warnings++;

            MelonLogger.Warning("[Know] " + reason
                + (_warnings == MaxWarnings ? $" Further ones will be counted rather than named ({_refused} so far)." : string.Empty));
        }

        private static void LearnRecipe(Actor player, in KnowledgeMessage message)
        {
            SubDatabase_Recipe? database = ProviderAccess.RecipeDatabase;
            if (database == null) return;

            RecipeContainer recipe = database.RecipeByGuid(message.Id);
            if (recipe == null)
            {
                // The other player may run a mod that adds recipes.
                Refuse($"{message.Learner} learned recipe '{message.Id}', which this game does not know. Mod sets may differ.");
                return;
            }

            ICraftingActorModule crafting = player.CraftingActorModule;
            if (crafting == null) return;

            IRecipeRepository repository = crafting.GetRecipeRepository();
            if (repository == null) return;

            // Adding one twice would announce it again.
            if (repository.HasRecipe(message.Id)) return;

            repository.AddRecipe(recipe.Cast<IRecipe>());

            MelonLogger.Msg($"[Know] learned recipe '{message.Id}' from {message.Learner}.");
        }

        private static void LearnWriting(in KnowledgeMessage message)
        {
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (database == null) return;

            Item item = database.GetItemByReadableId(message.Id);
            if (item == null)
            {
                Refuse($"{message.Learner} picked up '{message.Id}', which this game does not know. Mod sets may differ.");
                return;
            }

            if (!item.TryGetItemBehaviour(out ItemBhvr_Letter? letter) || letter == null || !letter.HasTopic) return;

            if (!PlayerMetaDataGameHandler.TryGet(out PlayerMetaDataGameHandler meta)) return;

            Journal journal = meta.GetJournal();
            if (journal == null) return;

            // Unread, since a teammate read it. False when the topic is already there.
            if (!journal.AddLetterTopic(letter.Topic, true, letter)) return;

            MelonLogger.Msg($"[Know] added the letter from '{message.Id}' to the journal, found by {message.Learner}.");
        }
    }
}
