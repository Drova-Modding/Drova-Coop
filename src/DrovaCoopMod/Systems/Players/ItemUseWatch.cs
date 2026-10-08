using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Players.Patches;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.ABI;
using Il2CppDrova.ABI.Damage;
using Il2CppDrova.Items;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Announces the local player's casts and makes spells cast by puppets harmless, since the caster's machine already applied their damage.
    /// Hooks <c>Item.StartUseItem</c> because casting is not an <c>EActionBind</c> and leaves no state a poll could read.
    /// </summary>
    internal sealed class ItemUseWatch : ICoopSystem
    {
        // Listed by hand: recipes and letters use the same funnel but are not gestures.
        private static readonly ItemSubCategory[] ReplicatedUses =
        [
            ItemSubCategory.Consumable_Potion,
            ItemSubCategory.Consumable_Food,
            ItemSubCategory.Consumable_Plant,
            ItemSubCategory.Consumable_RawFood,
            ItemSubCategory.Consumable_Throw,
        ];

        // The game calls EndUseItem every frame the button is up, so the release is matched against this.
        private static string? _casting;

        private static string? _pending;

        private static byte _sequence;

        private static readonly Dictionary<PlayerId, ItemUseSnapshot> ItemUsesByPlayer = new();

        /// <summary>The last ability each other player announced. Only the newest is kept, so bodies never work through a backlog.</summary>
        internal static IReadOnlyDictionary<PlayerId, ItemUseSnapshot> ItemUses => ItemUsesByPlayer;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // A prefix too, because a conjuring spell decides its batch inside StartUseItem and needs the cast number first. See SummonWatch.
            Hooking.TryPrefix(harmony, typeof(Item), "StartUseItem",
                typeof(ItemUseWatchPatches), nameof(ItemUseWatchPatches.StartUseItemPrefix));
            Hooking.TryPostfix(harmony, typeof(Item), "StartUseItem",
                typeof(ItemUseWatchPatches), nameof(ItemUseWatchPatches.StartUseItemPostfix));
            Hooking.TryPostfix(harmony, typeof(Item), "EndUseItem",
                typeof(ItemUseWatchPatches), nameof(ItemUseWatchPatches.EndUseItemPostfix));

            // Narrowest place to skip a puppet spell's damage: the hit, despawn and impact still play.
            Hooking.TryPrefix(harmony, typeof(ABI_Bhvr_HitEffect_Damage), "OnHit",
                typeof(ItemUseWatchPatches), nameof(ItemUseWatchPatches.HitEffectDamagePrefix));

            // Self-damage ticks such as bleeding.
            Hooking.TryPrefix(harmony, HarmonyLib.AccessTools.Method(typeof(ABI_Bhvr_Damage_Self), "TriggerTick", [typeof(int)]),
                typeof(ItemUseWatchPatches), nameof(ItemUseWatchPatches.SelfTickPrefix), "ABI_Bhvr_Damage_Self.TriggerTick(int)");

            // A puppet passes the is-player test, so its spell would aim at the local cursor instead of its facing.
            Hooking.TryPrefix(harmony, typeof(ABI_Ability), "HandlePlayerTargetPos",
                typeof(ItemUseWatchPatches), nameof(ItemUseWatchPatches.HandlePlayerTargetPosPrefix));
        }

        /// <summary>Forgets the cast in progress when the world goes away.</summary>
        private static void Reset()
        {
            _casting = null;
            _pending = null;
        }

        // Runs for every actor's item use, so the guards go cheapest first. A number is only burned for a use that is announced.
        internal static void StartUseItemPrefix(Item? __instance, Actor? actor)
        {
            _pending = null;

            try
            {
                if (RemoteApply<ItemUseScope>.InProgress) return;

                if (!CoopSession.IsLive) return;
                if (__instance == null || actor == null) return;
                if (!IsWorthReplicating(__instance)) return;
                if (!RemotePlayers.IsLocalPlayer(actor)) return;

                string id = __instance.ReadableId;
                if (string.IsNullOrEmpty(id)) return;

                _sequence = unchecked((byte)(_sequence + 1));
                _pending = id;

                SummonWatch.BeginCast(CoopSession.LocalPlayerId, _sequence, actor);
            }
            catch (Exception e)
            {
                _pending = null;

                MelonLogger.Error("[Cast] deciding whether a use is worth announcing failed: " + e);
            }
        }

        internal static void StartUseItemPostfix()
        {
            try
            {
                if (_pending == null) return;

                _casting = _pending;
                _pending = null;

                BroadcastItemUse(_casting, _sequence, false);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cast] watching a cast failed: " + e);
            }
        }

        internal static void EndUseItemPostfix(Item? __instance, Actor? actor)
        {
            try
            {
                if (RemoteApply<ItemUseScope>.InProgress) return;
                if (_casting == null) return;
                if (!CoopSession.IsLive) return;
                if (__instance == null || actor == null) return;

                // The selected ability can change between start and release, so match on the id.
                if (!string.Equals(__instance.ReadableId, _casting, StringComparison.Ordinal)) return;
                if (!RemotePlayers.IsLocalPlayer(actor)) return;

                string id = _casting;
                _casting = null;

                BroadcastItemUse(id, _sequence, true);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cast] watching the end of a cast failed: " + e);
            }
        }

        // An effect this player put on a puppet already ticks on its owner's machine.
        internal static bool SelfTickPrefix(ABI_Bhvr_Damage_Self __instance)
        {
            try
            {
                ABI_InstanceInfo? info = __instance.Controller?.AbilityInfo;
                if (info == null) return true;

                return !(RemotePlayers.IsPuppet(info.TargetActor) && RemotePlayers.IsLocalPlayer(info.GetOwner()));
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cast] deciding whether an effect's tick belongs to a puppet failed: " + e);
                return true;
            }
        }

        internal static bool HitEffectDamagePrefix(ABI_Bhvr_HitEffect_Damage __instance, Entity entity)
        {
            try
            {
                if (__instance == null) return true;

                ABI_Controller? controller = __instance.Controller;
                if (controller == null) return true;

                ABI_InstanceInfo? info =controller.AbilityInfo;
                if (info == null) return true;

                if (RemotePlayers.IsPuppet(info.GetOwner())) return false;

                // Only effects this player applied: StatusWatch replicates only those, so an enemy's poison lives only here.
                if (info.RelatedStatus != null && RemotePlayers.IsPuppet(entity) && RemotePlayers.IsLocalPlayer(info.GetOwner())) return false;

                return true;
            }
            catch (Exception e)
            {
                // Letting the hit through is the safe failure.
                MelonLogger.Error("[Cast] deciding whether a spell hit belongs to a puppet failed: " + e);
                return true;
            }
        }

        internal static bool HandlePlayerTargetPosPrefix(Entity owner)
        {
            try
            {
                return !RemotePlayers.IsPuppet(owner);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cast] deciding whether a spell was aimed by a puppet failed: " + e);
                return true;
            }
        }

        /// <summary>Whether this item's use is an ability or a visible consumption.</summary>
        internal static bool IsWorthReplicating(Item item)
        {
            ItemCategory? category =item.Category;
            if (category == null) return false;

            if (category.IsFlowAbility()) return true;

            ItemSubCategory sub = category.SubCategory;
            foreach (ItemSubCategory known in ReplicatedUses)
            {
                if (sub == known) return true;
            }

            return false;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<ItemUseMessage>(MessageIds.ItemUse, OnItemUse);
        }

        void ICoopSystem.EndSession()
        {
            ItemUsesByPlayer.Clear();
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            ItemUsesByPlayer.Remove(player);
        }

        /// <summary>Tells everyone the local player started or stopped using an ability. Reliable, since it is sent only on cast edges.</summary>
        /// <param name="abilityId">The readable id of the ability item.</param>
        /// <param name="sequence">Which cast this is, as the caller counted it.</param>
        /// <param name="released">Whether this is the end of the cast rather than the start.</param>
        private static void BroadcastItemUse(string abilityId, byte sequence, bool released)
        {
            if (!CoopSession.IsLive) return;
            if (string.IsNullOrEmpty(abilityId)) return;

            ItemUseMessage message = new()
            {
                User = CoopSession.LocalPlayerId,
                ItemId = abilityId,
                Sequence = sequence,
                Released = released ? (byte)1 : (byte)0,
            };

            NetworkAccess.SendToAll(message);
        }

        private static void OnItemUse(INetPeer peer, ItemUseMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                message.User = sender;
                Remember(sender, message);

                CoopSession.SendToClientsExcept(message, peer.Id);
                return;
            }

            if (!CoopSession.IsFromHost()) return;
            if (message.User == CoopSession.LocalPlayerId) return;

            Remember(message.User, message);
        }

        // Versioned because casting the same spell twice is two casts.
        private static void Remember(PlayerId player, in ItemUseMessage message)
        {
            int version = ItemUsesByPlayer.TryGetValue(player, out ItemUseSnapshot existing) ? existing.Version + 1 : 1;
            ItemUsesByPlayer[player] = new ItemUseSnapshot(message.ItemId ?? string.Empty, message.Sequence, message.Released != 0, version);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
