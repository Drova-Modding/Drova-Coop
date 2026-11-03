using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Loot.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.UI;
using Il2CppDrova;
using Il2CppDrova.GUI;
using Il2CppDrova.InventorySystem;
using Il2CppDrova.Items;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Loot
{
    /// <summary>
    /// Trading between two players in the merchant window, with a copy of the teammate's bag on their body as the trader.
    /// What moved when the window closes is the offer, applied on the teammate's machine only after their yes. Values need not balance.
    /// </summary>
    internal sealed class Trade : ICoopSystem
    {
        private const float Range = 400f;

        /// <summary>Seconds before a teammate's next ask is answered, since each answer sends this player's whole bag.</summary>
        private const float AskCooldown = 2f;

        private static readonly List<LootStack> BagArriving = [];

        private static readonly Dictionary<PlayerId, float> LastAskBy = new();

        /// <summary>Teammates whose offer is up as a popup here. One each, or a peer could stack popups.</summary>
        private static readonly HashSet<PlayerId> AwaitingAnswer = new();

        /// <summary>The partner's side of an offer arriving in pieces.</summary>
        private static readonly List<LootStack> GiveArriving = [];
        private static readonly List<LootStack> TakeArriving = [];

        private static ushort _nextId;
        private static ushort _open;
        private static PlayerId _partner = PlayerId.None;
        private static Actor? _puppet;
        private static Dictionary<string, int> _bagAtOpen = new(StringComparer.Ordinal);
        /// <summary>Watched by reference, since Escape destroys it and GetMerchantWindow would then spawn a blank new one.</summary>
        private static GUI_Window_Merchant? _window;

        /// <summary>Merchant rules are lifted only while the teammate window is up, never for NPC merchants.</summary>
        private static bool _shown;
        private static float _askedAt;

        /// <summary>The copy's bag after the last Trade press, to undo a trade cut short. Items move on each press.</summary>
        private static Dictionary<string, int>? _atAccept;

        private static ushort _offerArrivingId;
        private static PlayerId _offerArrivingFrom = PlayerId.None;

        /// <summary>The offer sent and not yet answered, kept to undo it on a no.</summary>
        private static ushort _offered;
        private static PlayerId _offeredTo = PlayerId.None;
        private static LootStack[] _offeredGive = [];
        private static LootStack[] _offeredTake = [];

        /// <summary>Trades finished on this machine's side. For tests.</summary>
        internal static int Completed { get; private set; }

        /// <summary>Trades refused or undone. For tests.</summary>
        internal static int Refused { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(Il2CppTradingSystem.MarketPlace), nameof(Il2CppTradingSystem.MarketPlace.CanCompleteTrading),
                typeof(TradePatches), nameof(TradePatches.CanCompletePrefix));

            // CompleteTrading's inner CanCompleteTrading call is inlined, so the move is made here too.
            Hooking.TryPrefix(harmony, typeof(Il2CppTradingSystem.MarketPlace), nameof(Il2CppTradingSystem.MarketPlace.CompleteTrading),
                typeof(TradePatches), nameof(TradePatches.CompleteTradingPrefix));

            // Skips the "gives more than it gets" warning, since the partner's answer is the confirmation.
            Hooking.TryPrefix(harmony, typeof(GUI_Window_Merchant), "Accept", typeof(TradePatches), nameof(TradePatches.AcceptPrefix));
        }

        internal static bool AcceptPrefix(GUI_Window_Merchant __instance)
        {
            if (!_shown) return true;

            __instance.CompleteTrading();
            return false;
        }

        internal static bool CompleteTradingPrefix(Il2CppTradingSystem.MarketPlace __instance, ref Il2CppTradingSystem.ResultState __result)
        {
            if (!_shown) return true;

            // The game's own two moves, both ways, without its value check.
            __instance.TradeItems(__instance._traderServiceLeft, __instance._traderLeft, __instance._traderServiceRight, __instance._traderRight);
            __instance.TradeItems(__instance._traderServiceRight, __instance._traderRight, __instance._traderServiceLeft, __instance._traderLeft);
            if (_puppet != null) _atAccept = Count(_puppet.GetInventory());
            __result = new Il2CppTradingSystem.ResultState(Il2CppTradingSystem.ResultStateType.Success, "Trade completed");
            return false;
        }

        internal static bool CanCompletePrefix(ref bool __result)
        {
            if (!_shown) return true;

            __result = true;
            return false;
        }

        /// <summary>Opens a trade with the nearest teammate, or the one whose name starts with <paramref name="who"/>. For <c>coop_trade</c>.</summary>
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
                if (body.Body == null) continue;
                if (who.Length > 0 && !(CoopSession.Players.TryGetValue(id, out string name) && name.StartsWith(who, StringComparison.OrdinalIgnoreCase))) continue;

                float distance = Vector2.Distance(body.Body.transform.position, player.transform.position);
                if (distance > bestDistance) continue;

                best = body;
                bestDistance = distance;
            }

            if (best == null) return $"no teammate within {Range:0} units to trade with.";

            return Begin(best);
        }

        /// <summary>Opens a trade with one teammate, from the talk menu on their body.</summary>
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
            _window = null;
            _askedAt = Time.unscaledTime;

            SendTrade(new TradeMessage { Id = _open, Stage = TradeMessage.StageAsk, To = _partner });
            return $"asking {CoopSession.NameOf(_partner)} to trade.";
        }

        /// <summary>A step arrived for this player.</summary>
        private static void Take(in TradeMessage message)
        {
            try
            {
                switch (message.Stage)
                {
                    case TradeMessage.StageAsk:
                        if (LastAskBy.TryGetValue(message.From, out float last) && Time.unscaledTime - last < AskCooldown) break;

                        LastAskBy[message.From] = Time.unscaledTime;
                        foreach (LootStack[] chunk in Bag().Chunk(TradeMessage.MaxStacks))
                        {
                            SendTrade(new TradeMessage { Id = message.Id, Stage = TradeMessage.StageBag, To = message.From, Take = chunk });
                        }

                        SendTrade(new TradeMessage { Id = message.Id, Stage = TradeMessage.StageBagEnd, To = message.From });
                        Announce($"{CoopSession.NameOf(message.From)} opened a trade with you.");
                        break;

                    case TradeMessage.StageBag when message.Id == _open && message.From == _partner && !_shown:
                        AddCapped(BagArriving, message.Take);
                        break;

                    case TradeMessage.StageBagEnd when message.Id == _open && message.From == _partner && !_shown:
                        ShowWindow([.. BagArriving]);
                        BagArriving.Clear();
                        break;

                    case TradeMessage.StageOffer:
                        // One offer at a time, by sender as well as id, since every opener counts ids from one.
                        if (message.Id != _offerArrivingId || message.From != _offerArrivingFrom)
                        {
                            if (_offerArrivingId != 0 && message.From != _offerArrivingFrom)
                            {
                                SendTrade(new TradeMessage { Id = message.Id, Stage = TradeMessage.StageNo, To = message.From });
                                break;
                            }

                            _offerArrivingId = message.Id;
                            _offerArrivingFrom = message.From;
                            GiveArriving.Clear();
                            TakeArriving.Clear();
                        }

                        AddCapped(GiveArriving, message.Give);
                        AddCapped(TakeArriving, message.Take);
                        break;

                    case TradeMessage.StageOfferEnd when message.Id == _offerArrivingId && message.From == _offerArrivingFrom:
                        TradeMessage whole = message with { Give = [.. GiveArriving], Take = [.. TakeArriving] };
                        _offerArrivingId = 0;
                        _offerArrivingFrom = PlayerId.None;
                        GiveArriving.Clear();
                        TakeArriving.Clear();
                        Ask(whole);
                        break;

                    case TradeMessage.StageYes when message.Id == _offered && message.From == _offeredTo:
                        Completed++;
                        _offered = 0;
                        Announce($"{CoopSession.NameOf(message.From)} accepted the trade.");
                        break;

                    case TradeMessage.StageNo when message.Id == _offered && message.From == _offeredTo:
                        Undo(_offeredGive, _offeredTake);
                        Announce($"{CoopSession.NameOf(message.From)} did not take the trade; your side is undone.");
                        break;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Trade] a trade step failed: " + e);
            }
        }

        /// <summary>Watches the open window. Its closing is the offer.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
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

            if (!_shown && Time.unscaledTime - _askedAt > 10f)
            {
                Close();
                Announce("No answer from your trade partner.");
                return;
            }

            if (!_shown || (_window != null && _window.IsWindowOpen)) return;

            Dictionary<string, int> now = Count(_puppet.GetInventory());
            List<LootStack> give = [];
            List<LootStack> take = [];
            foreach (string id in now.Keys.Union(_bagAtOpen.Keys))
            {
                int delta = now.GetValueOrDefault(id) - _bagAtOpen.GetValueOrDefault(id);
                if (delta > 0) give.Add(new LootStack { Id = id, Amount = delta });
                else if (delta < 0) take.Add(new LootStack { Id = id, Amount = -delta });
            }

            ushort offerId = _open;
            PlayerId partner = _partner;
            Close();

            MelonLogger.Msg($"[Trade] the window closed: giving {give.Count} and taking {take.Count} kind(s) of item.");
            if (give.Count == 0 && take.Count == 0) return;

            _offered = offerId;
            _offeredTo = partner;
            _offeredGive = [.. give];
            _offeredTake = [.. take];

            int pieces = Math.Max((give.Count + TradeMessage.MaxStacks - 1) / TradeMessage.MaxStacks, (take.Count + TradeMessage.MaxStacks - 1) / TradeMessage.MaxStacks);
            for (int piece = 0; piece < pieces; piece++)
            {
                SendTrade(new TradeMessage
                {
                    Id = offerId,
                    Stage = TradeMessage.StageOffer,
                    To = partner,
                    Give = [.. give.Skip(piece * TradeMessage.MaxStacks).Take(TradeMessage.MaxStacks)],
                    Take = [.. take.Skip(piece * TradeMessage.MaxStacks).Take(TradeMessage.MaxStacks)],
                });
            }

            SendTrade(new TradeMessage { Id = offerId, Stage = TradeMessage.StageOfferEnd, To = partner });
            Announce($"Offer sent to {CoopSession.NameOf(partner)}; waiting for their answer.");
        }

        /// <summary>Ends any trade and undoes an unanswered offer.</summary>
        internal static void Reset()
        {
            LastAskBy.Clear();
            AwaitingAnswer.Clear();
            Abort();
            if (_offered != 0) Undo(_offeredGive, _offeredTake);
        }

        /// <summary>Ends a trade cut short. What the Trade presses moved is put back and the window closes.</summary>
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
                if (_window != null && _window.IsWindowOpen) _window.CloseWindow();
            }
            catch (Exception)
            {
                // Best effort, since the trade is already settled or undone above.
            }

            Close();
        }

        private static void ShowWindow(LootStack[] bag)
        {
            if (_puppet == null || !GUIGameHandler.TryGet(out GUIGameHandler gui) || gui == null)
            {
                Close();
                return;
            }

            Inventory? inventory = _puppet.GetInventory();
            if (inventory == null)
            {
                Close();
                return;
            }

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
            _window = window;
            _shown = true;
            MelonLogger.Msg($"[Trade] trading with {CoopSession.NameOf(_partner)}: {bag.Length} stack(s) on their side.");
        }

        /// <summary>The partner's side, a yes/no popup over the offer and then the exchange.</summary>
        private static void Ask(TradeMessage offer)
        {
            if (AwaitingAnswer.Contains(offer.From) || !IsKnown(offer.Give) || !IsKnown(offer.Take))
            {
                Refused++;
                SendTrade(new TradeMessage { Id = offer.Id, Stage = TradeMessage.StageNo, To = offer.From });
                return;
            }

            string text = $"{CoopSession.NameOf(offer.From)} offers: {Describe(offer.Give)}\nand wants: {Describe(offer.Take)}\nAccept?";

            if (!GUIGameHandler.TryGet(out GUIGameHandler gui) || gui == null)
            {
                Answer(offer, false);
                return;
            }

            GUI_IConfirmPopUp popup = gui.OpenGenericConfirmPopUp();
            popup.SetDescText(text);
            popup.SetYesText("Accept");
            popup.SetNoText("Refuse");
            AwaitingAnswer.Add(offer.From);
            popup.AddCallback(new Action<ConfirmPopUpResultArgs>(result => Answer(offer, result.Result == ConfirmPopUpResult.Yes)));
        }

        /// <summary>Whether every stack names an item this game has, so the popup shows exactly what would move.</summary>
        private static bool IsKnown(LootStack[]? stacks)
        {
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (database == null) return false;

            foreach (LootStack stack in stacks ?? [])
            {
                if (stack.Amount <= 0 || database.GetItemByReadableId(stack.Id) == null) return false;
            }

            return true;
        }

        private static void AddCapped(List<LootStack> into, LootStack[]? stacks)
        {
            foreach (LootStack stack in stacks ?? [])
            {
                if (into.Count >= CoopSession.MaxGathered) return;

                into.Add(stack);
            }
        }

        private static void Answer(TradeMessage offer, bool yes)
        {
            AwaitingAnswer.Remove(offer.From);

            bool done = yes && Exchange(offer);
            if (done) Completed++;
            else Refused++;

            SendTrade(new TradeMessage { Id = offer.Id, Stage = done ? TradeMessage.StageYes : TradeMessage.StageNo, To = offer.From, Give = [], Take = [] });
        }

        /// <summary>Applies the offer to this bag, only if everything taken is still here.</summary>
        private static bool Exchange(TradeMessage offer)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return false;
            Inventory? inventory = player.GetInventory();
            SubDatabase_Item? database = ProviderAccess.ItemDatabase;
            if (inventory == null || database == null) return false;

            // Checked against the tradeable bag summed over stacks, the way it was offered.
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

            MelonLogger.Msg($"[Trade] traded with {CoopSession.NameOf(offer.From)}: got {Describe(offer.Give)}, gave {Describe(offer.Take)}.");
            return true;
        }

        /// <summary>Puts the opener's side of a refused trade back.</summary>
        private static void Undo(LootStack[] gave, LootStack[] took)
        {
            Refused++;
            _offered = 0;
            _offeredTo = PlayerId.None;

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;
            Inventory? inventory = player.GetInventory();
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

        /// <summary>This player's tradeable bag. Everything but what is worn, quest items included.</summary>
        private static LootStack[] Bag()
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return [];

            Actor.EquipmentModule? equipment = player.GetEquipmentModule();
            Inventory? inventory = player.GetInventory();
            if (inventory == null) return [];

            List<LootStack> bag = [];
            foreach (ItemStack stack in inventory.InventoryItems)
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
                Item? item = stack?.Item;
                string? id = item == null ? null : item.ReadableId;
                if (id == null || stack!.Amount <= 0) continue;
                counts[id] = counts.GetValueOrDefault(id) + stack.Amount;
            }

            return counts;
        }

        /// <summary>Removes that many across stacks, since RemoveItem takes from the first stack only.</summary>
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
                try
                {
                    // The bag held what the body wears too, so emptying it undressed the body.
                    Empty(_puppet.GetInventory());
                    if (RemotePlayers.Bodies.TryGetValue(_partner, out RemotePlayer? body)) body?.Redress();
                }
                catch (Exception)
                {
                    // Best effort, since the puppet's bag is a copy and goes with the window.
                }
            }

            _open = 0;
            _partner = PlayerId.None;
            _puppet = null;
            _bagAtOpen.Clear();
            BagArriving.Clear();
            _window = null;
            _shown = false;
            _atAccept = null;
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

        private static void Announce(string text)
        {
            Overlay.Show(text);

            MelonLogger.Msg("[Trade] " + text);
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<TradeMessage>(MessageIds.Trade, OnTrade);
        }

        /// <summary>Sends a trade step for one player, through the host or straight from it.</summary>
        private static void SendTrade(TradeMessage message)
        {
            message.Give ??= [];
            message.Take ??= [];
            message = message with { From = CoopSession.LocalPlayerId };
            if (CoopSession.State == CoopSessionState.Hosting) CoopSession.TrySendToPlayer(message, message.To);
            else if (CoopSession.IsLive) NetworkAccess.SendToAll(message);
        }

        private static void OnTrade(INetPeer peer, TradeMessage message)
        {
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                // Who is trading is the connection, not the payload.
                if (!CoopSession.TryGetSender(peer, out PlayerId sender) || message.To == sender) return;
                message = message with { From = sender };

                if (message.To == CoopSession.LocalPlayerId) Take(message);
                else CoopSession.TrySendToPlayer(message, message.To);
                return;
            }

            if (CoopSession.IsFromHost() && message.To == CoopSession.LocalPlayerId) Take(message);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
