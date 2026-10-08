using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.Crafting;
using Il2CppDrova.GlobalVarSystem;
using Il2CppDrova.Items;
using Il2CppDrova.JournalSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Teaches the local player recipes and letters a teammate learned. The far half of <see cref="KnowledgeWatch"/>.
    /// </summary>
    internal sealed class RemoteKnowledge : ICoopSystem
    {
        // Caps per-frame work so a peer cannot keep the bounded queue topped up into unbounded work.
        private const int MaxPerFrame = 8;

        // Warnings embed peer-chosen text, so only the first few are logged and the rest counted.
        private const int MaxWarnings = 10;

        /// <summary>Unlearned things held before further ones are refused. Nothing restates them, so none may be dropped.</summary>
        private const int MaxPendingKnowledge = 256;

        // No dedup: the game refuses a recipe it already has.
        private static readonly Queue<KnowledgeMessage> IncomingKnowledge = new();

        private static int _warnings;
        private static int _refused;

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            for (int taught = 0; taught < MaxPerFrame; taught++)
            {
                if (!TryTakeKnowledge(out KnowledgeMessage message)) return;

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

            RecipeContainer? recipe =database.RecipeByGuid(message.Id);
            if (recipe == null)
            {
                // The other player may run a mod that adds recipes.
                Refuse($"{message.Learner} learned recipe '{message.Id}', which this game does not know. Mod sets may differ.");
                return;
            }

            ICraftingActorModule? crafting = player.CraftingActorModule;
            if (crafting == null) return;

            IRecipeRepository? repository =crafting.GetRecipeRepository();
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

            Item? item = database.GetItemByReadableId(message.Id);
            if (item == null)
            {
                Refuse($"{message.Learner} picked up '{message.Id}', which this game does not know. Mod sets may differ.");
                return;
            }

            if (!item.TryGetItemBehaviour(out ItemBhvr_Letter? letter) || letter == null || !letter.HasTopic) return;

            if (!PlayerMetaDataGameHandler.TryGet(out PlayerMetaDataGameHandler meta)) return;

            Journal? journal =meta.GetJournal();
            if (journal == null) return;

            // Unread, since a teammate read it. False when the topic is already there.
            if (!journal.AddLetterTopic(letter.Topic, true, letter)) return;

            MelonLogger.Msg($"[Know] added the letter from '{message.Id}' to the journal, found by {message.Learner}.");
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<KnowledgeMessage>(MessageIds.Knowledge, OnKnowledge);
        }

        void ICoopSystem.EndSession()
        {
            IncomingKnowledge.Clear();
        }

        /// <summary>Tells everyone the local player learned something, like a recipe whose paper is gone afterwards.</summary>
        internal static void BroadcastKnowledge(byte kind, string id)
        {
            if (!CoopSession.IsLive) return;
            if (string.IsNullOrEmpty(id)) return;

            KnowledgeMessage message = new()
            {
                Learner = CoopSession.LocalPlayerId,
                Kind = kind,
                Id = id,
            };

            NetworkAccess.SendToAll(message);
        }

        /// <summary>Takes the next thing learned elsewhere that this player has not been taught yet.</summary>
        private static bool TryTakeKnowledge(out KnowledgeMessage message)
            => IncomingKnowledge.TryDequeue(out message);

        private static void OnKnowledge(INetPeer peer, KnowledgeMessage message)
        {
            if (!CoopSession.IsLive) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                message.Learner = sender;

                // Relayed first so a third player does not wait for the host's world to be ready.
                CoopSession.SendToClientsExcept(message, peer.Id);
            }
            else if (!CoopSession.IsFromHost() || message.Learner == CoopSession.LocalPlayerId)
            {
                return;
            }

            // Refused, never dropping the oldest: nothing repeats a recipe. The cap only bounds a hostile peer.
            if (!CoopSession.Enqueue(IncomingKnowledge, message, MaxPendingKnowledge))
            {
                MelonLogger.Warning($"[Know] more than {MaxPendingKnowledge} things are waiting to be learned; " +
                    $"'{message.Id}' from {message.Learner} was refused and is lost.");
            }
        }
    }
}
