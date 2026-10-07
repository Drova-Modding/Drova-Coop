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
    /// Hands everybody the items a dialogue granted one player, once per node identity per player.
    /// Trades and nodes that equip their item stay local.
    /// </summary>
    internal static class ItemGrantShare
    {
        /// <summary>Grant nodes already paid here, persisted in the savegame because a reload clears session memory.</summary>
        private static readonly GrantLedger Ledger = new("CoopGrant_");

        /// <summary>How many handovers this savegame has recorded. For <c>coop_status</c>.</summary>
        internal static int Remembered => Ledger.Remembered;

        /// <summary>How many repeats the savegame's own memory caught. For <c>coop_status</c>.</summary>
        internal static int RememberedHits => Ledger.Hits;

        /// <summary>How many notes the savegame refused. Above zero means a repeat is still possible.</summary>
        internal static int RefusedWrites => Ledger.RefusedWrites;

        /// <summary>How many grants this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared { get; private set; }

        /// <summary>How many grants arrived and were given here. For <c>coop_status</c>.</summary>
        internal static int Received { get; private set; }

        /// <summary>Forgets the counts and what was granted. Called when a session ends.</summary>
        internal static void Reset()
        {
            Ledger.Reset();
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

        /// <summary>Gives everything that has arrived. Call once per frame from a loaded world.</summary>
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

                // Already granted this session. Silent, since two copies of one conversation are the ordinary case.
                if (Ledger.IsKnown(message.Identity)) return;

                // Paid in an earlier session. Logged as proof the note survived a reload.
                if (Ledger.WasPaid(message.Identity))
                {
                    Ledger.Claim(message.Identity);

                    MelonLogger.Msg("[Items] a dialogue handover this savegame already collected was " +
                        "skipped; it was paid in an earlier session and would otherwise arrive twice.");

                    return;
                }

                Ledger.Claim(message.Identity);

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
                        // The other player may run a mod whose items this game lacks.
                        MelonLogger.Warning($"[Items] a teammate's dialogue granted '{stack.Id}', which this game does not know. Mod sets may differ.");
                        continue;
                    }

                    inventory.AddItem(new ItemStack(item, stack.Amount));
                    Received++;
                    given++;

                    MelonLogger.Msg($"[Items] {stack.Amount}x '{stack.Id}' arrived from a teammate's dialogue.");
                }

                // Remembered only if something landed, so a failed grant stays repeatable.
                if (given > 0) Ledger.Remember(message.Identity);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Items] giving a teammate's dialogue items failed: " + e);
            }
        }

        /// <summary>
        /// Lifts the player-bound stacks out of a node whose grant already arrived here. The postfix puts them back.
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

                bool thisSession = Ledger.IsKnown(identity);
                if (!thisSession && !Ledger.WasPaid(identity)) return;

                for (int index = __instance.ItemStacks.Count - 1; index >= 0; index--)
                {
                    DialogItemsExchange entry = __instance.ItemStacks[index];
                    if (entry == null || !IsPlayerGrant(entry)) continue;

                    (__state ??= new List<DialogItemsExchange>()).Insert(0, entry);
                    __instance.ItemStacks.RemoveAt(index);
                }

                if (__state != null)
                {
                    // Which memory caught it matters: only the savegame one proves the note survived a reload.
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

                // The prefix lifted a repeat's stacks out: put the authored entries back and report nothing.
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
                if (string.IsNullOrEmpty(identity) || Ledger.IsKnown(identity)) return;

                // Before the grant gate, since a take-only node never sets _playerGotItem.
                // Its success is connectionID 0 (player had the goods), 1 otherwise.
                if (IsHandIn(__instance))
                {
                    if (__instance.connectionID == 0) ReportHandIn(__instance, identity);
                    return;
                }

                if (!__instance._playerGotItem) return;
                if (Ledger.WasPaid(identity)) return;

                // An equipping node dresses the player for their own story beat (miner shirt, intro swords).
                // A relayed copy would make the player's own node skip as a repeat.
                if (__instance._equip) return;

                List<LootStack> stacks = new();

                for (int index = 0; index < __instance.ItemStacks.Count; index++)
                {
                    DialogItemsExchange entry = __instance.ItemStacks[index];
                    if (entry == null) continue;

                    // Anything leaving the player makes the node a trade, which stays local.
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

                // _playerGotItem confirms the local player has the goods.
                Ledger.Claim(identity);
                Ledger.Remember(identity);
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

        /// <summary>Marks a message as a hand-in, so teammates drop the same quest items.</summary>
        private const string HandInPrefix = "handin:";

        /// <summary>How many relayed quest items were taken back after a teammate handed theirs in.</summary>
        internal static int HandedIn { get; private set; }

        /// <summary>
        /// Tells teammates which quest items this dialogue took, so their relayed copies do not reopen a finished choice.
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
            // Once per node, so the same hand-in reported twice does not take two copies.
            if (Ledger.IsKnown(message.Identity)) return;

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            Inventory? inventory = player.GetInventory();
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (inventory == null || database == null) return;

            Ledger.Claim(message.Identity);

            foreach (LootStack stack in message.Stacks)
            {
                if (string.IsNullOrEmpty(stack.Id) || stack.Amount <= 0) continue;

                Item item = database.GetItemByReadableId(stack.Id);

                // Checked on this machine's own item data, so the sender cannot make a sellable item count as a quest item.
                if (item == null || !IsQuestItem(item)) continue;

                // Unequipped first, as the game's hand-in does, or a worn ring leaves its slot and stats behind.
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

        /// <summary>A quest item by either game test (quest category, or zero value), since they disagree on sellable quest gear.</summary>
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
