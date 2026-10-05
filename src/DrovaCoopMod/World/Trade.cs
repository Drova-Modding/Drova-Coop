using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.GUI;
using Il2CppDrova.InventorySystem;
using Il2CppDrova.Items;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Trading between two players, in the game's own merchant window with the teammate's body as the trader.
    ///
    /// The opener asks the teammate for their bag (worn items left out; quest items included), fills the teammate's body here
    /// with it and opens the window. Whatever the opener then does at the table happens to that copy; when the window
    /// closes, what moved is the offer. Nothing leaves the teammate's bag until they say yes to it in a popup - and
    /// still hold what is taken - so the copy can never spend what its owner no longer has. A no undoes the opener's
    /// side. Values do not have to balance: between friends a gift is a trade, so the window's "you get more than
    /// you give" refusal is lifted while a teammate trade is open. Coins are items like any other (the window's own
    /// money balancing is off).
    /// </summary>
    internal static class Trade
    {
        /// <summary>How close the teammate has to stand.</summary>
        private const float Range = 400f;

        private static ushort _nextId;
        private static ushort _open;
        private static PlayerId _partner = PlayerId.None;
        private static Actor? _puppet;
        private static Dictionary<string, int> _bagAtOpen = new(StringComparer.Ordinal);
        private static bool _windowSeen;

        // The teammate window is up: only then are the merchant rules lifted. A trade waiting for the bag - or stuck,
        // when the bag never comes - must not make every NPC merchant free.
        private static bool _shown;
        private static float _askedAt;

        // The copy's bag after the last Trade press. Items move on each press, not when the window closes, so a trade cut
        // short (the teammate leaves, the world reloads) is undone from this.
        private static Dictionary<string, int>? _atAccept;
        private static readonly List<LootStack> _bagArriving = [];

        // The partner's side of an offer arriving in pieces.
        private static ushort _offerArrivingId;
        private static PlayerId _offerArrivingFrom = PlayerId.None;
        private static readonly List<LootStack> _giveArriving = [];
        private static readonly List<LootStack> _takeArriving = [];

        // The offer sent and not yet answered, kept to undo it on a no.
        private static ushort _offered;
        private static PlayerId _offeredTo = PlayerId.None;
        private static LootStack[] _offeredGive = [];
        private static LootStack[] _offeredTake = [];

        /// <summary>Trades finished on this machine's side. For tests.</summary>
        internal static int Completed { get; private set; }

        /// <summary>Trades refused or undone. For tests.</summary>
        internal static int Refused { get; private set; }

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(Il2CppTradingSystem.MarketPlace), nameof(Il2CppTradingSystem.MarketPlace.CanCompleteTrading),
                typeof(Trade), nameof(CanCompletePrefix));

            // CompleteTrading asks CanCompleteTrading again inside, and that call is inlined: the patch above never runs
            // there, and a trade that took more than it gave was dropped without a word. So the move is made here too.
            Hooking.TryPrefix(harmony, typeof(Il2CppTradingSystem.MarketPlace), nameof(Il2CppTradingSystem.MarketPlace.CompleteTrading),
                typeof(Trade), nameof(CompleteTradingPrefix));

            // The window warns before a trade that gives more than it gets. Between teammates that is a gift, and the
            // partner's own yes or no is the confirmation that counts - so the warning is skipped.
            Hooking.TryPrefix(harmony, typeof(GUI_Window_Merchant), "Accept", typeof(Trade), nameof(AcceptPrefix));
        }

        private static bool AcceptPrefix(GUI_Window_Merchant __instance)
        {
            if (!_shown) return true;

            __instance.CompleteTrading();
            return false;
        }

        private static bool CompleteTradingPrefix(Il2CppTradingSystem.MarketPlace __instance, ref Il2CppTradingSystem.ResultState __result)
        {
            if (!_shown) return true;

            // The game's own two moves, both ways, without its value check.
            __instance.TradeItems(__instance._traderServiceLeft, __instance._traderLeft, __instance._traderServiceRight, __instance._traderRight);
            __instance.TradeItems(__instance._traderServiceRight, __instance._traderRight, __instance._traderServiceLeft, __instance._traderLeft);
            if (_puppet != null) _atAccept = Count(_puppet.GetInventory());
            __result = new Il2CppTradingSystem.ResultState(Il2CppTradingSystem.ResultStateType.Success, "Trade completed");
            return false;
        }

        private static bool CanCompletePrefix(ref bool __result)
        {
            if (!_shown) return true;

            __result = true;
            return false;
        }

        /// <summary>coop_trade: opens a trade with the nearest teammate, or the one whose name starts with <paramref name="who"/>.</summary>
        internal static string Open(string? who)
        {
            who ??= string.Empty;
            if (!CoopSession.IsLive) return "no session.";
            if (_open != 0 || _offered != 0) return "a trade is already under way.";
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return "no player.";

            RemotePlayer? best = null;
            float bestDistance = Range;
            foreach ((PlayerId id, RemotePlayer body) in RemotePlayers.Bodies)
            {
                if (body?.Body == null) continue;
                if (who.Length > 0 && !(CoopSession.Players.TryGetValue(id, out string? name) && name.StartsWith(who, StringComparison.OrdinalIgnoreCase))) continue;

                float distance = Vector2.Distance(body.Body.transform.position, player.transform.position);
                if (distance > bestDistance) continue;

                best = body;
                bestDistance = distance;
            }

            if (best == null) return $"no teammate within {Range:0} units to trade with.";

            return Begin(best);
        }

        /// <summary>Opens a trade with one teammate - from the talk menu on their body.</summary>
        internal static string OpenWith(PlayerId partner)
        {
            if (!CoopSession.IsLive) return "no session.";
            if (_open != 0 || _offered != 0) return "a trade is already under way.";
            if (!RemotePlayers.Bodies.TryGetValue(partner, out RemotePlayer? body) || body?.Body == null) return "that teammate is not here.";

            return Begin(body);
        }

        private static string Begin(RemotePlayer best)
        {
            _open = ++_nextId == 0 ? ++_nextId : _nextId;
            _partner = best.Id;
            _puppet = best.Body;
            _windowSeen = false;
            _askedAt = Time.unscaledTime;

            Send(new TradeMessage { Id = _open, Stage = TradeMessage.StageAsk, To = _partner });
            return $"asking {NameOf(_partner)} to trade.";
        }

        /// <summary>A step arrived for this player.</summary>
        internal static void Take(in TradeMessage message)
        {
            try
            {
                switch (message.Stage)
                {
                    case TradeMessage.StageAsk:
                        foreach (LootStack[] chunk in Bag().Chunk(TradeMessage.MaxStacks))
                        {
                            Send(new TradeMessage { Id = message.Id, Stage = TradeMessage.StageBag, To = message.From, Take = chunk });
                        }

                        Send(new TradeMessage { Id = message.Id, Stage = TradeMessage.StageBagEnd, To = message.From });
                        Announce($"{NameOf(message.From)} opened a trade with you.");
                        break;

                    case TradeMessage.StageBag when message.Id == _open && message.From == _partner:
                        _bagArriving.AddRange(message.Take ?? []);
                        break;

                    case TradeMessage.StageBagEnd when message.Id == _open && message.From == _partner:
                        ShowWindow([.. _bagArriving]);
                        _bagArriving.Clear();
                        break;

                    case TradeMessage.StageOffer:
                        // One offer at a time, by sender as well as id: every opener counts its ids from one, and two
                        // offers arriving at once were merged into one.
                        if (message.Id != _offerArrivingId || message.From != _offerArrivingFrom)
                        {
                            if (_offerArrivingId != 0 && message.From != _offerArrivingFrom)
                            {
                                Send(new TradeMessage { Id = message.Id, Stage = TradeMessage.StageNo, To = message.From });
                                break;
                            }

                            _offerArrivingId = message.Id;
                            _offerArrivingFrom = message.From;
                            _giveArriving.Clear();
                            _takeArriving.Clear();
                        }

                        _giveArriving.AddRange(message.Give ?? []);
                        _takeArriving.AddRange(message.Take ?? []);
                        break;

                    case TradeMessage.StageOfferEnd when message.Id == _offerArrivingId && message.From == _offerArrivingFrom:
                        TradeMessage whole = message;
                        whole.Give = [.. _giveArriving];
                        whole.Take = [.. _takeArriving];
                        _offerArrivingId = 0;
                        _offerArrivingFrom = PlayerId.None;
                        _giveArriving.Clear();
                        _takeArriving.Clear();
                        Ask(whole);
                        break;

                    case TradeMessage.StageYes when message.Id == _offered && message.From == _offeredTo:
                        Completed++;
                        _offered = 0;
                        Announce($"{NameOf(message.From)} accepted the trade.");
                        break;

                    case TradeMessage.StageNo when message.Id == _offered && message.From == _offeredTo:
                        Undo(_offeredGive, _offeredTake);
                        Announce($"{NameOf(message.From)} did not take the trade; your side is undone.");
                        break;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Trade] a trade step failed: " + e);
            }
        }

        /// <summary>Watches the open window; its closing is the offer. Call once per frame.</summary>
        internal static void Update()
        {
            // An offer whose partner left before answering is undone: nobody is ever going to say no.
            if (_offered != 0 && !CoopSession.Players.ContainsKey(_offeredTo))
            {
                Undo(_offeredGive, _offeredTake);
                Announce("Your trade partner left before answering; your side is undone.");
            }

            if (_open == 0) return;

            if (!CoopSession.IsLive || _puppet == null)
            {
                Abort();
                return;
            }

            // The bag never came (the partner's side failed, or they left as it was asked for): not under way for ever.
            if (!_shown && Time.unscaledTime - _askedAt > 10f)
            {
                Close();
                Announce("No answer from your trade partner.");
                return;
            }

            if (!GUIGameHandler.TryGet(out GUIGameHandler gui) || gui == null) return;
            GUI_Window_Merchant window = gui.GetMerchantWindow();
            bool open = window != null && window.IsWindowOpen;

            if (open)
            {
                _windowSeen = _shown;
                return;
            }

            if (!_windowSeen) return;

            // Closed: what the teammate's copy gained is given, what it lost is taken.
            Dictionary<string, int> now = Count(_puppet.GetInventory());
            List<LootStack> give = [];
            List<LootStack> take = [];
            foreach (string id in now.Keys.Union(_bagAtOpen.Keys))
            {
                int delta = now.GetValueOrDefault(id) - _bagAtOpen.GetValueOrDefault(id);
                if (delta > 0) give.Add(new LootStack { Id = id, Amount = delta });
                else if (delta < 0) take.Add(new LootStack { Id = id, Amount = -delta });
            }

            ushort id2 = _open;
            PlayerId partner = _partner;
            Close();

            MelonLogger.Msg($"[Trade] the window closed: giving {give.Count} and taking {take.Count} kind(s) of item.");
            if (give.Count == 0 && take.Count == 0) return;

            _offered = id2;
            _offeredTo = partner;
            _offeredGive = [.. give];
            _offeredTake = [.. take];

            // In pieces, like the bag: a relay forwards so much per message, and a table has no limit.
            int pieces = Math.Max((give.Count + TradeMessage.MaxStacks - 1) / TradeMessage.MaxStacks, (take.Count + TradeMessage.MaxStacks - 1) / TradeMessage.MaxStacks);
            for (int piece = 0; piece < pieces; piece++)
            {
                Send(new TradeMessage
                {
                    Id = id2,
                    Stage = TradeMessage.StageOffer,
                    To = partner,
                    Give = [.. give.Skip(piece * TradeMessage.MaxStacks).Take(TradeMessage.MaxStacks)],
                    Take = [.. take.Skip(piece * TradeMessage.MaxStacks).Take(TradeMessage.MaxStacks)],
                });
            }

            Send(new TradeMessage { Id = id2, Stage = TradeMessage.StageOfferEnd, To = partner });
            Announce($"Offer sent to {NameOf(partner)}; waiting for their answer.");
        }

        /// <summary>Forgets everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            Abort();
            if (_offered != 0) Undo(_offeredGive, _offeredTake);
        }

        /// <summary>
        /// Ends a trade cut short while its window was up: what the Trade presses already moved is put back, and the
        /// window closes - left open, it would trade on the game's own rules with a copy nobody owns.
        /// </summary>
        private static void Abort()
        {
            if (_atAccept != null)
            {
                List<LootStack> gave = [];
                List<LootStack> took = [];
                foreach (string id in _atAccept.Keys.Union(_bagAtOpen.Keys))
                {
                    int delta = _atAccept.GetValueOrDefault(id) - _bagAtOpen.GetValueOrDefault(id);
                    if (delta > 0) gave.Add(new LootStack { Id = id, Amount = delta });
                    else if (delta < 0) took.Add(new LootStack { Id = id, Amount = -delta });
                }

                Undo([.. gave], [.. took]);
                Announce("The trade was cut short; your side is undone.");
            }

            try
            {
                if (_shown && GUIGameHandler.TryGet(out GUIGameHandler gui) && gui != null && gui.GetMerchantWindow() is { IsWindowOpen: true } window) window.CloseWindow();
            }
            catch (Exception) { }

            Close();
        }

        private static void ShowWindow(LootStack[] bag)
        {
            if (_puppet == null || !GUIGameHandler.TryGet(out GUIGameHandler gui) || gui == null)
            {
                Close();
                return;
            }

            Inventory inventory = _puppet.GetInventory();
            Empty(inventory);

            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            foreach (LootStack stack in bag)
            {
                Item? item = database?.GetItemByReadableId(stack.Id);
                if (item != null && stack.Amount > 0) inventory.AddItem(new ItemStack(item, stack.Amount));
            }

            _bagAtOpen = Count(inventory);

            GUI_Window_Merchant window = gui.GetMerchantWindow();
            window.Init(_puppet, false);
            window.ShowWindow();
            _shown = true;
            MelonLogger.Msg($"[Trade] trading with {NameOf(_partner)}: {bag.Length} stack(s) on their side.");
        }

        /// <summary>The partner's side: a yes/no popup over the offer, then the exchange.</summary>
        private static void Ask(TradeMessage offer)
        {
            string text = $"{NameOf(offer.From)} offers: {Describe(offer.Give)}\nand wants: {Describe(offer.Take)}\nAccept?";

            if (!GUIGameHandler.TryGet(out GUIGameHandler gui) || gui == null)
            {
                Answer(offer, false);
                return;
            }

            GUI_IConfirmPopUp popup = gui.OpenGenericConfirmPopUp();
            popup.SetDescText(text);
            popup.SetYesText("Accept");
            popup.SetNoText("Refuse");
            popup.AddCallback(new Action<ConfirmPopUpResultArgs>(result => Answer(offer, result.Result == ConfirmPopUpResult.Yes)));
        }

        private static void Answer(TradeMessage offer, bool yes)
        {
            bool done = yes && Exchange(offer);
            if (done) Completed++;
            else Refused++;

            Send(new TradeMessage { Id = offer.Id, Stage = done ? TradeMessage.StageYes : TradeMessage.StageNo, To = offer.From, Give = [], Take = [] });
        }

        /// <summary>Takes what was taken out of this bag and puts in what was given - only if all of it is still here.</summary>
        private static bool Exchange(TradeMessage offer)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return false;
            Inventory inventory = player.GetInventory();
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (inventory == null || database == null) return false;

            // Against the bag as it may be traded, summed over stacks: an item since equipped, or held as two single
            // stacks, is checked the way it was offered.
            Dictionary<string, int> tradeable = new(StringComparer.Ordinal);
            foreach (LootStack stack in Bag()) tradeable[stack.Id] = tradeable.GetValueOrDefault(stack.Id) + stack.Amount;

            foreach (LootStack stack in offer.Take ?? [])
            {
                if (database.GetItemByReadableId(stack.Id) == null || tradeable.GetValueOrDefault(stack.Id) < stack.Amount)
                {
                    Announce("The trade no longer fits your bag; refused.");
                    return false;
                }
            }

            foreach (LootStack stack in offer.Take ?? []) RemoveAll(inventory, database.GetItemByReadableId(stack.Id), stack.Amount);
            foreach (LootStack stack in offer.Give ?? [])
            {
                Item? item = database.GetItemByReadableId(stack.Id);
                if (item != null) inventory.AddItem(new ItemStack(item, stack.Amount));
            }

            MelonLogger.Msg($"[Trade] traded with {NameOf(offer.From)}: got {Describe(offer.Give)}, gave {Describe(offer.Take)}.");
            return true;
        }

        /// <summary>The opener's side of a refused trade, put back: what was taken goes, what was given returns.</summary>
        private static void Undo(LootStack[] gave, LootStack[] took)
        {
            Refused++;
            _offered = 0;
            _offeredTo = PlayerId.None;

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;
            Inventory inventory = player.GetInventory();
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (inventory == null || database == null) return;

            foreach (LootStack stack in took)
            {
                Item? item = database.GetItemByReadableId(stack.Id);
                if (item != null) RemoveAll(inventory, item, stack.Amount);
            }

            foreach (LootStack stack in gave)
            {
                Item? item = database.GetItemByReadableId(stack.Id);
                if (item != null) inventory.AddItem(new ItemStack(item, stack.Amount));
            }
        }

        /// <summary>
        /// This player's bag as it may be traded: everything but what is worn. Quest items too - when a story hands one to
        /// the wrong player, a trade is how the party puts it right.
        /// </summary>
        private static LootStack[] Bag()
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return [];

            Actor.EquipmentModule? equipment = player.GetEquipmentModule();
            List<LootStack> bag = [];
            foreach (ItemStack stack in player.GetInventory().InventoryItems)
            {
                Item? item = stack?.Item;
                if (item == null || stack!.Amount <= 0 || string.IsNullOrEmpty(item.ReadableId)) continue;
                if (equipment?.GetSlotsWithItem(item) is { Count: > 0 }) continue;

                bag.Add(new LootStack { Id = item.ReadableId, Amount = stack.Amount });
                if (bag.Count >= 240) break;
            }

            return [.. bag];
        }

        private static Dictionary<string, int> Count(Inventory? inventory)
        {
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            if (inventory == null) return counts;

            foreach (ItemStack stack in inventory.InventoryItems)
            {
                string? id = stack?.Item?.ReadableId;
                if (id == null || stack!.Amount <= 0) continue;
                counts[id] = counts.GetValueOrDefault(id) + stack.Amount;
            }

            return counts;
        }

        /// <summary>
        /// Removes that many, across stacks: RemoveItem takes from the first stack of an item only, and items that do not
        /// stack are held as one stack each.
        /// </summary>
        private static void RemoveAll(Inventory inventory, Item item, int amount)
        {
            while (amount > 0)
            {
                int removed = inventory.RemoveItem(item, amount, false);
                if (removed <= 0) return;
                amount -= removed;
            }
        }

        private static void Empty(Inventory? inventory)
        {
            if (inventory == null) return;

            List<(Item Item, int Amount)> all = [];
            foreach (ItemStack stack in inventory.InventoryItems)
            {
                if (stack?.Item != null && stack.Amount > 0) all.Add((stack.Item, stack.Amount));
            }

            foreach ((Item item, int amount) in all) RemoveAll(inventory, item, amount);
        }

        private static void Close()
        {
            if (_puppet != null)
            {
                try { Empty(_puppet.GetInventory()); } catch (Exception) { }
            }

            _open = 0;
            _partner = PlayerId.None;
            _puppet = null;
            _bagAtOpen.Clear();
            _bagArriving.Clear();
            _windowSeen = false;
            _shown = false;
            _atAccept = null;
        }

        private static void Send(TradeMessage message)
        {
            message.Give ??= [];
            message.Take ??= [];
            CoopSession.SendTrade(message);
        }

        private static string Describe(LootStack[]? stacks)
        {
            if (stacks == null || stacks.Length == 0) return "nothing";

            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            return string.Join(", ", stacks.Select(stack =>
            {
                Item? item = database?.GetItemByReadableId(stack.Id);
                string name = item != null ? item.GetName() : stack.Id;
                return stack.Amount == 1 ? name : $"{stack.Amount}x {name}";
            }));
        }

        private static string NameOf(PlayerId player)
        {
            return CoopSession.Players.TryGetValue(player, out string? name) && !string.IsNullOrEmpty(name) ? name : $"player {player.Value}";
        }

        private static void Announce(string text)
        {
            try
            {
                if (GUIGameHandler.TryGet(out GUIGameHandler gui) && gui != null) gui.GetOverlayTextHandler()?.CreateOverlayText(text);
            }
            catch (Exception) { }

            MelonLogger.Msg("[Trade] " + text);
        }
    }
}
