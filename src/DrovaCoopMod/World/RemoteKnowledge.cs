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
    /// Teaches the local player what the others have learned.
    ///
    /// The far half of <see cref="KnowledgeWatch"/>. Nothing here is a copy of an item: the recipe is
    /// added to this player's own book and the letter to this player's own journal, so both read exactly
    /// as they would had this player found the paper themselves.
    ///
    /// Done from the frame loop rather than where the message arrives, for the reason everything here is:
    /// the game may only be touched from the frame loop, and knowledge routinely arrives before there is
    /// a player to teach.
    /// </summary>
    internal static class RemoteKnowledge
    {
        /// <summary>
        /// How many things are taught in one frame.
        ///
        /// The queue is bounded, but draining all of it every frame made that bound meaningless: a peer
        /// could keep it topped up faster than it filled and have this machine do unbounded work, and log
        /// unbounded lines, forever. Real play never has more than one or two waiting.
        /// </summary>
        private const int MaxPerFrame = 8;

        /// <summary>
        /// How many things this machine will complain about before it stops.
        ///
        /// Every warning below embeds text the sender chose, and MelonLoader writes its log to disk as it
        /// goes - so one line per message is a way for somebody else to fill this disk and stall these
        /// frames. The first few are worth having, because a genuine mod-set mismatch shows up in them
        /// immediately; past that the count is the only thing worth keeping.
        /// </summary>
        private const int MaxWarnings = 10;

        private static int _warnings;
        private static int _refused;

        /// <summary>
        /// Teaches everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return;

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
                // The guard, so the watch on the other side does not read this as something the local
                // player discovered and announce it back to the player it came from.
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

                        // Anything else is a peer on a later version teaching something this build has no
                        // way to apply. Ignored rather than guessed at, and said once so a mismatched pair
                        // is diagnosable.
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

        /// <summary>
        /// Says why something could not be taught, for the first few, and counts the rest.
        ///
        /// The tally is what makes a flood diagnosable without being the flood's delivery mechanism: the
        /// same reasoning <see cref="CoopSession"/> applies to refused player states, and for the same
        /// reason - this path is reachable by any peer in the session.
        /// </summary>
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
                // A real possibility rather than a defensive check: the other player may be running a mod
                // that adds recipes this game does not have.
                Refuse($"{message.Learner} learned recipe '{message.Id}', which this game does not know. Mod sets may differ.");
                return;
            }

            ICraftingActorModule crafting = player.CraftingActorModule;
            if (crafting == null) return;

            IRecipeRepository repository = crafting.GetRecipeRepository();
            if (repository == null) return;

            // Asked first, because adding one twice would announce it to the world a second time on
            // machines whose guard has already been left.
            if (repository.HasRecipe(message.Id)) return;

            // Cast rather than passed straight in: the interop exposes the interface and the concrete
            // asset as unrelated proxy types, so the conversion the C# compiler would make for free on the
            // game's own types has to be asked for here.
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

            // Unread, because this player has not read it - their friend did. The game answers false when
            // the topic is already there, which is what makes teaching the same letter twice harmless.
            if (!journal.AddLetterTopic(letter.Topic, true, letter)) return;

            MelonLogger.Msg($"[Know] added the letter from '{message.Id}' to the journal, found by {message.Learner}.");
        }
    }
}
