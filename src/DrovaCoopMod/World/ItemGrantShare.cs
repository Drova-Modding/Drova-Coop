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
        /// <summary>Grant nodes already paid here, by dialogue tree plus node id.</summary>
        private static readonly HashSet<string> _granted = new(StringComparer.Ordinal);

        private static int _shared;
        private static int _received;

        /// <summary>How many grants this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Shared => _shared;

        /// <summary>How many grants arrived and were given here. For <c>coop_status</c>.</summary>
        internal static int Received => _received;

        /// <summary>Forgets the counts and what was granted. Called when a session ends.</summary>
        internal static void Reset()
        {
            _granted.Clear();
            _shared = 0;
            _received = 0;
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
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
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

                // Already granted - by this player sitting through the conversation themselves or by
                // an earlier copy of this grant.
                if (!_granted.Add(message.Identity)) return;

                if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return;

                Inventory? inventory = player.GetInventory();
                SubDatabase_Item? database = ProviderAccess.ItemDatabase;
                if (inventory == null || database == null) return;

                foreach (DialogueItemsMessage.Stack stack in message.Stacks)
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
                    _received++;

                    MelonLogger.Msg($"[Items] {stack.Amount}x '{stack.Id}' arrived from a teammate's dialogue.");
                }
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
                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (__instance == null || __instance.ItemStacks == null) return;

                string identity = IdentityOf(__instance);
                if (string.IsNullOrEmpty(identity) || !_granted.Contains(identity)) return;

                for (int index = __instance.ItemStacks.Count - 1; index >= 0; index--)
                {
                    DialogItemsExchange entry = __instance.ItemStacks[index];
                    if (entry == null || !IsPlayerGrant(entry)) continue;

                    (__state ??= new List<DialogItemsExchange>()).Insert(0, entry);
                    __instance.ItemStacks.RemoveAt(index);
                }

                if (__state != null)
                {
                    MelonLogger.Msg("[Items] a dialogue handover this world already collected was " +
                        "skipped, because a teammate's copy of the same conversation granted it first.");
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

                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (__instance.ItemStacks == null || !__instance._playerGotItem) return;

                string identity = IdentityOf(__instance);
                if (string.IsNullOrEmpty(identity) || _granted.Contains(identity)) return;

                List<DialogueItemsMessage.Stack> stacks = new();

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

                    stacks.Add(new DialogueItemsMessage.Stack
                    {
                        Id = entry.Item.ReadableId,
                        Amount = Math.Min(amount, DialogueItemsMessage.MaxAmount),
                    });
                }

                if (stacks.Count == 0) return;

                _granted.Add(identity);
                _shared++;

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

        private static bool IsPlayerGrant(DialogItemsExchange entry)
        {
            return entry.Exchange == DialogItemsExchange.ExchangeDirection.VoidToPlayer
                || entry.Exchange == DialogItemsExchange.ExchangeDirection.NPCToPlayer;
        }

        /// <summary>
        /// Names an exchange node so every machine names it the same. Node.ID is authored into the
        /// graph asset; Node.UID is minted at runtime on most nodes and differs per machine.
        /// </summary>
        private static string IdentityOf(DS_GiveItemNode node)
        {
            try
            {
                return (node.DLGTree?.name ?? "?") + "#" + node.ID;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
