using Drova_Modding_API;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.ABI;
using Il2CppDrova.ABI.Damage;
using Il2CppDrova.Items;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Announces the local player's casts and makes spells cast by puppets harmless, since the caster's machine already applied their damage.
    /// Hooks <c>Item.StartUseItem</c> because casting is not an <c>EActionBind</c> and leaves no state a poll could read.
    /// </summary>
    internal static class ItemUseWatch
    {
        // The game calls EndUseItem every frame the button is up, so the release is matched against this.
        private static string? _casting;

        private static string? _pending;

        private static byte _sequence;

        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // A prefix too, because a conjuring spell decides its batch inside StartUseItem and needs the cast number first. See SummonWatch.
            Hooking.TryPrefix(harmony, typeof(Item), "StartUseItem",
                typeof(ItemUseWatch), nameof(StartUseItemPrefix));
            Hooking.TryPostfix(harmony, typeof(Item), "StartUseItem",
                typeof(ItemUseWatch), nameof(StartUseItemPostfix));
            Hooking.TryPostfix(harmony, typeof(Item), "EndUseItem",
                typeof(ItemUseWatch), nameof(EndUseItemPostfix));

            // Narrowest place to skip a puppet spell's damage: the hit, despawn and impact still play.
            Hooking.TryPrefix(harmony, typeof(ABI_Bhvr_HitEffect_Damage), "OnHit",
                typeof(ItemUseWatch), nameof(HitEffectDamagePrefix));

            // Self-damage ticks such as bleeding.
            Hooking.TryPrefix(harmony, HarmonyLib.AccessTools.Method(typeof(ABI_Bhvr_Damage_Self), "TriggerTick", new[] { typeof(int) }),
                typeof(ItemUseWatch), nameof(SelfTickPrefix), "ABI_Bhvr_Damage_Self.TriggerTick(int)");

            // A puppet passes the is-player test, so its spell would aim at the local cursor instead of its facing.
            Hooking.TryPrefix(harmony, typeof(ABI_Ability), "HandlePlayerTargetPos",
                typeof(ItemUseWatch), nameof(HandlePlayerTargetPosPrefix));
        }

        /// <summary>Forgets the cast in progress when the world goes away.</summary>
        internal static void Reset()
        {
            _casting = null;
            _pending = null;
        }

        // Runs for every actor's item use, so the guards go cheapest first. A number is only burned for a use that is announced.
        private static void StartUseItemPrefix(Item __instance, Actor actor)
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

        private static void StartUseItemPostfix()
        {
            try
            {
                if (_pending == null) return;

                _casting = _pending;
                _pending = null;

                CoopSession.BroadcastItemUse(_casting, _sequence, false);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cast] watching a cast failed: " + e);
            }
        }

        private static void EndUseItemPostfix(Item __instance, Actor actor)
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

                CoopSession.BroadcastItemUse(id, _sequence, true);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cast] watching the end of a cast failed: " + e);
            }
        }

        // An effect this player put on a puppet already ticks on its owner's machine.
        private static bool SelfTickPrefix(ABI_Bhvr_Damage_Self __instance)
        {
            try
            {
                ABI_InstanceInfo? info = __instance?.Controller?.AbilityInfo;
                if (info == null) return true;

                return !(RemotePlayers.IsPuppet(info.TargetActor) && RemotePlayers.IsLocalPlayer(info.GetOwner()));
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cast] deciding whether an effect's tick belongs to a puppet failed: " + e);
                return true;
            }
        }

        private static bool HitEffectDamagePrefix(ABI_Bhvr_HitEffect_Damage __instance, Entity entity)
        {
            try
            {
                if (__instance == null) return true;

                ABI_Controller controller = __instance.Controller;
                if (controller == null) return true;

                ABI_InstanceInfo info = controller.AbilityInfo;
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

        private static bool HandlePlayerTargetPosPrefix(Entity owner)
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

        // Listed by hand: recipes and letters use the same funnel but are not gestures.
        private static readonly ItemSubCategory[] ReplicatedUses =
        [
            ItemSubCategory.Consumable_Potion,
            ItemSubCategory.Consumable_Food,
            ItemSubCategory.Consumable_Plant,
            ItemSubCategory.Consumable_RawFood,
            ItemSubCategory.Consumable_Throw,
        ];

        /// <summary>Whether this item's use is an ability or a visible consumption.</summary>
        internal static bool IsWorthReplicating(Item item)
        {
            ItemCategory category = item.Category;
            if (category == null) return false;

            if (category.IsFlowAbility()) return true;

            ItemSubCategory sub = category.SubCategory;
            foreach (ItemSubCategory known in ReplicatedUses)
            {
                if (sub == known) return true;
            }

            return false;
        }
    }
}
