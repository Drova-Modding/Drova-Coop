using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.DialogueNew;
using Il2CppDrova.InventorySystem;
using Il2CppDrova.Items;
using Il2CppNodeCanvas.DialogueTrees;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Hands everybody the items a dialogue granted one player.
    ///
    /// A quest handover fills the hands of the player standing in the conversation - the boss gives
    /// out one pickaxe, and the teammate working the same mine has nothing to dig with. Grants
    /// travel under the same node identity dialogue experience uses, so each node pays every player
    /// exactly once: the arriving grant is refused where the identity is known, and a local sit-through
    /// of an already-granted node has its player-bound stacks lifted out for the duration of the node.
    ///
    /// Only pure grants travel. A node that also moves something out of the player is a trade, and
    /// a teammate who paid nothing has bought nothing.
    /// </summary>
    internal static class ItemGrantShare
    {
        /// <summary>
        /// Grant nodes already paid here, by dialogue tree plus node id, for this session and written
        /// into the savegame so it outlives a reload.
        ///
        /// A savegame load clears the session memory. That is what makes an authored
        /// one-off arrive twice: after a reload the teammate whose copy of the conversation never ran
        /// takes the branch, their node fires against an empty set, and the goods are handed out again.
        /// The identity is savegame-stable already, so remembering it is the whole fix.
        /// </summary>
        private static readonly GrantLedger _ledger = new("CoopGrant_");

        /// <summary>How many handovers this savegame has recorded. For <c>coop_status</c>.</summary>
        internal static int Remembered => _ledger.Remembered;

        /// <summary>How many repeats the savegame's own memory caught. For <c>coop_status</c>.</summary>
        internal static int RememberedHits => _ledger.Hits;

        /// <summary>How many notes the savegame refused. Above zero means a repeat is still possible.</summary>
        internal static int RefusedWrites => _ledger.RefusedWrites;

        /// <summary>How many grants this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared { get; private set; }

        /// <summary>How many grants arrived and were given here. For <c>coop_status</c>.</summary>
        internal static int Received { get; private set; }

        /// <summary>Forgets the counts and what was granted. Called when a session ends.</summary>
        internal static void Reset()
        {
            _ledger.Reset();
            Shared = 0;
            Received = 0;
        }

        /// <summary>Applies the exchange-node patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(DS_GiveItemNode), "OnExecute",
                typeof(ItemGrantShare), nameof(GiveItemPrefix));
            Hooking.TryPostfix(harmony, typeof(DS_GiveItemNode), "OnExecute",
                typeof(ItemGrantShare), nameof(GiveItemPostfix));
        }

        /// <summary>
        /// Gives everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive)
            {
                Reset();
                return;
            }

            while (CoopSession.TryTakeDialogueItems(out DialogueItemsMessage message))
            {
                Apply(message);
            }
        }

        private static void Apply(in DialogueItemsMessage message)
        {
            try
            {
                if (message.Stacks == null || message.Stacks.Length == 0) return;
                if (string.IsNullOrEmpty(message.Identity)) return;

                if (message.Identity.StartsWith(HandInPrefix, StringComparison.Ordinal))
                {
                    ApplyHandIn(message);
                    return;
                }

                // Already granted - by this player sitting through the conversation themselves, by an
                // earlier copy of this grant, or in a session before the last reload. The savegame is
                // asked first because the in-memory set does not survive one.
                // This session, and silently: two copies of one conversation are the ordinary case.
                if (_ledger.IsKnown(message.Identity)) return;

                // An earlier session. Worth a line, because it is the only evidence that the note
                // survived a reload - which is the whole of what this remembers for.
                if (_ledger.WasPaid(message.Identity))
                {
                    _ledger.Claim(message.Identity);

                    MelonLogger.Msg("[Items] a dialogue handover this savegame already collected was " +
                        "skipped; it was paid in an earlier session and would otherwise arrive twice.");

                    return;
                }

                _ledger.Claim(message.Identity);

                if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

                Inventory? inventory = player.GetInventory();
                SubDatabase_Item? database = ProviderAccess.ItemDatabase;
                if (inventory == null || database == null) return;

                int given = 0;

                foreach (LootStack stack in message.Stacks)
                {
                    if (string.IsNullOrEmpty(stack.Id)) continue;
                    if (stack.Amount <= 0 || stack.Amount > DialogueItemsMessage.MaxAmount) continue;

                    Item item = database.GetItemByReadableId(stack.Id);
                    if (item == null)
                    {
                        // A real possibility rather than a defensive check: the other player may be
                        // running a mod whose items this game does not have.
                        MelonLogger.Warning($"[Items] a teammate's dialogue granted '{stack.Id}', which this game does not know. Mod sets may differ.");
                        continue;
                    }

                    inventory.AddItem(new ItemStack(item, stack.Amount));
                    Received++;
                    given++;

                    MelonLogger.Msg($"[Items] {stack.Amount}x '{stack.Id}' arrived from a teammate's dialogue.");
                }

                // Written down only now, and only if something actually landed. The in-memory set is
                // claimed above the player and database lookups so a burst cannot double-pay within a
                // frame, but a grant that failed for want of a body or an item must stay repeatable -
                // a node remembered after a failed grant is an item the player can never get.
                if (given > 0) _ledger.Remember(message.Identity);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Items] giving a teammate's dialogue items failed: " + e);
            }
        }

        /// <summary>
        /// Lifts the player-bound stacks out of a node whose grant this world has already received,
        /// so the dialogue continues without filling the same hands twice. The postfix puts them back.
        /// </summary>
        private static void GiveItemPrefix(DS_GiveItemNode __instance,
            out List<DialogItemsExchange>? __state)
        {
            __state = null;

            try
            {
                if (!CoopSession.IsLive) return;
                if (__instance == null || __instance.ItemStacks == null) return;

                string identity = GrantLedger.IdentityOf(__instance);
                if (string.IsNullOrEmpty(identity)) return;

                bool thisSession = _ledger.IsKnown(identity);
                if (!thisSession && !_ledger.WasPaid(identity)) return;

                for (int index = __instance.ItemStacks.Count - 1; index >= 0; index--)
                {
                    DialogItemsExchange entry = __instance.ItemStacks[index];
                    if (entry == null || !IsPlayerGrant(entry)) continue;

                    (__state ??= new List<DialogItemsExchange>()).Insert(0, entry);
                    __instance.ItemStacks.RemoveAt(index);
                }

                if (__state != null)
                {
                    // Which memory caught it is worth saying: only the savegame one proves the note
                    // survived a reload, and that is the whole of what this change added.
                    MelonLogger.Msg(thisSession
                        ? "[Items] a dialogue handover this world already collected was skipped, " +
                          "because a teammate's copy of the same conversation granted it first."
                        : "[Items] a dialogue handover this savegame already collected was skipped; " +
                          "it was paid in an earlier session and would otherwise arrive a second time.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Items] refusing a repeated dialogue handover failed: " + e);
            }
        }

        private static void GiveItemPostfix(DS_GiveItemNode __instance, List<DialogItemsExchange>? __state)
        {
            try
            {
                if (__instance == null) return;

                // The prefix lifted a repeat's stacks out: put the authored entries back and report
                // nothing.
                if (__state != null)
                {
                    foreach (DialogItemsExchange entry in __state)
                    {
                        __instance.ItemStacks.Add(entry);
                    }

                    return;
                }

                if (!CoopSession.IsLive) return;
                if (__instance.ItemStacks == null) return;

                string identity = GrantLedger.IdentityOf(__instance);
                if (string.IsNullOrEmpty(identity) || _ledger.IsKnown(identity)) return;

                // Before the grant gate: a node that only takes never sets _playerGotItem, which is
                // the game's word for the player receiving something. Its own success is the item
                // check it ran - connectionID 0 when the player had the goods, 1 when not.
                if (IsHandIn(__instance))
                {
                    if (__instance.connectionID == 0) ReportHandIn(__instance, identity);
                    return;
                }

                if (!__instance._playerGotItem) return;
                if (_ledger.WasPaid(identity)) return;

                List<LootStack> stacks = new();

                for (int index = 0; index < __instance.ItemStacks.Count; index++)
                {
                    DialogItemsExchange entry = __instance.ItemStacks[index];
                    if (entry == null) continue;

                    // Anything leaving the player makes the node a trade, and a trade stays local:
                    // handing the goods to a teammate who paid nothing duplicates them.
                    if (entry.Exchange == DialogItemsExchange.ExchangeDirection.PlayerToNPC
                        || entry.Exchange == DialogItemsExchange.ExchangeDirection.PlayerToVoid)
                    {
                        return;
                    }

                    if (!IsPlayerGrant(entry) || entry.Item == null) continue;

                    int amount = entry.GetAmount(__instance, null);
                    if (amount <= 0) continue;

                    stacks.Add(new LootStack
                    {
                        Id = entry.Item.ReadableId,
                        Amount = Math.Min(amount, DialogueItemsMessage.MaxAmount),
                    });
                }

                if (stacks.Count == 0) return;

                // The local player demonstrably has the goods here - _playerGotItem is the game's own
                // word for it - so this one is safe to write down.
                _ledger.Claim(identity);
                _ledger.Remember(identity);
                Shared++;

                CoopSession.ReportDialogueItems(new DialogueItemsMessage
                {
                    Identity = identity,
                    Stacks = stacks.ToArray(),
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Items] reporting a dialogue handover failed: " + e);
            }
        }

        /// <summary>
        /// Marks a message as a hand-in: the quest items named left the sender's hands, and a teammate
        /// holding the same quest items drops them.
        /// </summary>
        private const string HandInPrefix = "handin:";

        /// <summary>How many relayed quest items were taken back after a teammate handed theirs in.</summary>
        internal static int HandedIn { get; private set; }

        /// <summary>
        /// Tells teammates which quest items this dialogue took from the player.
        ///
        /// A quest item handed out in dialogue crosses to every teammate, so after one player hands it
        /// in the others still carry a copy - a ring for Tuz that could still go to Andarta, a sealed
        /// letter for a finished errand, and a choice the quest already made reopened in their bags.
        /// Only quest items, which exist for the story alone; anything with a value stays where it is.
        /// </summary>
        private static bool IsHandIn(DS_GiveItemNode node)
        {
            for (int index = 0; index < node.ItemStacks.Count; index++)
            {
                DialogItemsExchange entry = node.ItemStacks[index];
                if (entry != null && (entry.Exchange == DialogItemsExchange.ExchangeDirection.PlayerToNPC
                    || entry.Exchange == DialogItemsExchange.ExchangeDirection.PlayerToVoid)) return true;
            }

            return false;
        }

        private static void ReportHandIn(DS_GiveItemNode node, string identity)
        {
            List<LootStack> stacks = new();

            for (int index = 0; index < node.ItemStacks.Count; index++)
            {
                DialogItemsExchange entry = node.ItemStacks[index];
                if (entry?.Item == null) continue;
                if (entry.Exchange != DialogItemsExchange.ExchangeDirection.PlayerToNPC
                    && entry.Exchange != DialogItemsExchange.ExchangeDirection.PlayerToVoid) continue;
                if (!IsQuestItem(entry.Item)) continue;

                int amount = entry.GetAmount(node, null);
                if (amount <= 0) continue;

                stacks.Add(new LootStack
                {
                    Id = entry.Item.ReadableId,
                    Amount = Math.Min(amount, DialogueItemsMessage.MaxAmount),
                });
            }

            if (stacks.Count == 0) return;

            CoopSession.ReportDialogueItems(new DialogueItemsMessage
            {
                Identity = HandInPrefix + identity,
                Stacks = stacks.ToArray(),
            });
        }

        private static void ApplyHandIn(in DialogueItemsMessage message)
        {
            // Once per session per node: the same hand-in reported twice must not take two copies.
            if (_ledger.IsKnown(message.Identity)) return;

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            Inventory? inventory = player.GetInventory();
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (inventory == null || database == null) return;

            _ledger.Claim(message.Identity);

            foreach (LootStack stack in message.Stacks)
            {
                if (string.IsNullOrEmpty(stack.Id) || stack.Amount <= 0) continue;

                Item item = database.GetItemByReadableId(stack.Id);

                // Checked again here, on this machine's own item data: the sender's word that
                // something is a quest item is not a licence to take a sellable one.
                if (item == null || !IsQuestItem(item)) continue;

                // Unequipped first, the way the game's own hand-in does: a worn quest ring removed
                // from the bag alone leaves its slot and its stats behind.
                Il2CppSystem.Collections.Generic.List<ActorEquipSlot>? slots =
                    player.GetEquipmentModule()?.GetSlotsWithItem(item);
                if (slots != null)
                {
                    for (int index = 0; index < slots.Count; index++)
                    {
                        slots[index]?.Unequip();
                    }
                }

                int taken = inventory.RemoveItem(item, Math.Min(stack.Amount, DialogueItemsMessage.MaxAmount), false);
                if (taken <= 0) continue;

                HandedIn++;
                MelonLogger.Msg($"[Items] a teammate handed in '{stack.Id}'; this player's copy went with it.");
            }
        }

        /// <summary>
        /// A quest item by either of the game's two tests: authored into the quest category, or worth
        /// nothing to buy or sell. They disagree on sellable quest gear, so both count.
        /// </summary>
        private static bool IsQuestItem(Item item)
        {
            return item.IsInQuestCategory || item.IsQuestItem;
        }

        private static bool IsPlayerGrant(DialogItemsExchange entry)
        {
            return entry.Exchange == DialogItemsExchange.ExchangeDirection.VoidToPlayer
                || entry.Exchange == DialogItemsExchange.ExchangeDirection.NPCToPlayer;
        }
    }
}
