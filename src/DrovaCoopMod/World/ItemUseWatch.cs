using Drova_Modding_API;
using Drova_Modding_API.Access;
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
    /// Notices when the local player casts, and keeps the spells other players' bodies cast from doing
    /// any harm.
    ///
    /// **Casting is not an action, which is why nothing else already carries it.** Every other thing a
    /// player does in combat is an <c>EActionBind</c>, and the poll in <see cref="ActionBinds"/> covers
    /// all eight of them. A spell is not among them: the game reads the selected ability out of
    /// <c>Inventory_Ability</c> and calls <c>Item.StartUseItem</c> on it, beside the action bindings
    /// entirely. So this hooks the item rather than the action module.
    ///
    /// **Hooked rather than polled, unusually for this mod.** A cast leaves nothing behind to sample:
    /// the instant abilities start no action at all, and the animated ones start one whose bind is
    /// <c>None</c>. There is no state a poll could read that says "a spell was cast this frame", only the
    /// call itself. <c>Item.StartUseItem</c> is also a genuine funnel - every path, player or AI, quick
    /// slot or cast button, goes through it - which is the one thing that makes a patch here worth the
    /// risk that a patch does not fire.
    ///
    /// **The other half of this class stops damage counting twice.** A spell replicated onto a remote
    /// body is spawned by this machine and hits whatever it overlaps here, while the caster's own machine
    /// already resolved that same spell against its copy of the victim and reported the result as an
    /// ordinary blow - see <see cref="DamageWatch"/>. Both landing would double every spell. The
    /// authoritative one is the caster's, so the copy is made harmless.
    /// </summary>
    internal static class ItemUseWatch
    {
        // What the local player is currently casting, as a readable item id, or null between casts. The
        // release has to be matched against it because the game calls EndUseItem every frame the cast
        // button is not held - not once when a cast ends - so without this the wire would carry a release
        // per frame forever.
        private static string? _casting;

        private static byte _sequence;

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(Item), "StartUseItem",
                typeof(ItemUseWatch), nameof(StartUseItemPostfix));
            Hooking.TryPostfix(harmony, typeof(Item), "EndUseItem",
                typeof(ItemUseWatch), nameof(EndUseItemPostfix));

            // The spell a puppet casts is a duplicate of one that already did its damage on the machine
            // that cast it. This is the narrowest place to stop it: the hit still happens, so the
            // projectile still despawns on contact and still plays its impact, and only the health change
            // is skipped.
            Hooking.TryPrefix(harmony, typeof(ABI_Bhvr_HitEffect_Damage), "OnHit",
                typeof(ItemUseWatch), nameof(HitEffectDamagePrefix));

            // A puppet is a clone of the player prefab and answers to the game's own is-this-the-player
            // test, which is what makes the ability spawner overwrite the aim with this machine's mouse
            // position. A remote player's spell would then fly at the local cursor rather than at whatever
            // they were aiming at. Skipping the override leaves the direction their body is already facing,
            // which the state stream keeps current.
            Hooking.TryPrefix(harmony, typeof(ABI_Ability), "HandlePlayerTargetPos",
                typeof(ItemUseWatch), nameof(HandlePlayerTargetPosPrefix));
        }

        /// <summary>
        /// Forgets the cast in progress. Called when the world goes away, so a cast that was never
        /// released does not suppress the first release of the next session.
        /// </summary>
        internal static void Reset()
        {
            _casting = null;
        }

        /// <summary>
        /// Announces the local player's cast.
        ///
        /// Runs for every item every actor in the game uses, including every potion an NPC drinks, so the
        /// guards are ordered cheapest first.
        /// </summary>
        private static void StartUseItemPostfix(Item __instance, Actor actor)
        {
            try
            {
                // The cast this mod itself just started on somebody else's body, arriving back through the
                // funnel it was always going to arrive back through.
                if (RemoteApply<ItemUseScope>.InProgress) return;

                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (__instance == null || actor == null) return;
                if (!IsWorthReplicating(__instance)) return;
                if (!IsLocalPlayer(actor)) return;

                string id = __instance.ReadableId;
                if (string.IsNullOrEmpty(id)) return;

                _sequence = unchecked((byte)(_sequence + 1));
                _casting = id;

                CoopSession.BroadcastItemUse(id, _sequence, false);
            }
            catch (Exception e)
            {
                // Swallowed rather than allowed out. This runs inside the game's own item-use path, and an
                // exception escaping it would take the cast with it - a coop bug that reads as the local
                // player's own spells sometimes failing.
                MelonLogger.Error("[Cast] watching a cast failed: " + e);
            }
        }

        /// <summary>
        /// Announces the end of the local player's cast, once per cast.
        /// </summary>
        private static void EndUseItemPostfix(Item __instance, Actor actor)
        {
            try
            {
                if (RemoteApply<ItemUseScope>.InProgress) return;
                if (_casting == null) return;
                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (__instance == null || actor == null) return;

                // Only the item whose start was announced. The game offers a release for the selected
                // ability every frame the button is up, and the selected ability can change between the
                // start and the release, so matching on the id is what keeps one cast to one pair.
                if (!string.Equals(__instance.ReadableId, _casting, StringComparison.Ordinal)) return;
                if (!IsLocalPlayer(actor)) return;

                string id = _casting;
                _casting = null;

                CoopSession.BroadcastItemUse(id, _sequence, true);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Cast] watching the end of a cast failed: " + e);
            }
        }

        /// <summary>
        /// Skips the health change when the spell belongs to a body this mod puppets.
        /// </summary>
        /// <returns>False to suppress the original, which is what makes the copy harmless.</returns>
        private static bool HitEffectDamagePrefix(ABI_Bhvr_HitEffect_Damage __instance, Entity entity)
        {
            try
            {
                if (__instance == null) return true;

                ABI_Controller controller = __instance.Controller;
                if (controller == null) return true;

                ABI_InstanceInfo info = controller.AbilityInfo;
                if (info == null) return true;

                // The spell a puppet cast, which is a copy of one that already did its damage on the
                // machine that cast it.
                if (RemotePlayers.IsPuppet(info.GetOwner())) return false;

                // **A status effect ticking on a puppet, which is the same duplicate wearing a different
                // hat.** Effects deal their damage by spawning an ability, and an effect this player
                // applied is replicated to the body's owner - so their own copy is already taking this
                // health off them on their own machine. Letting the puppet's copy tick as well would
                // report every tick as a blow and poison them at twice the rate.
                //
                // **Only the ones this player applied**, because only those are replicated. StatusWatch
                // refuses to carry an effect whose source is anything else, so an enemy's poison exists
                // nowhere but on this machine's puppet - and suppressing that one would not stop a
                // duplicate, it would delete the effect outright and leave remote players immune to every
                // enemy's damage over time.
                if (info.RelatedStatus != null && RemotePlayers.IsPuppet(entity) && IsLocalPlayer(info.GetOwner())) return false;

                return true;
            }
            catch (Exception e)
            {
                // Letting the hit through is the safe failure here. Suppressing damage on an exception
                // would make every spell in the game harmless the moment this went wrong, including the
                // player's own and every enemy's.
                MelonLogger.Error("[Cast] deciding whether a spell hit belongs to a puppet failed: " + e);
                return true;
            }
        }

        /// <summary>
        /// Skips the aim override for a spell cast by a body this mod puppets.
        /// </summary>
        /// <returns>False to suppress the original, leaving the aim the caller passed in.</returns>
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

        /// <summary>
        /// The kinds of item whose use is worth showing on another screen: what a player drinks, eats or
        /// throws.
        ///
        /// Enumerated by hand rather than swept from the enum, and deliberately short. Everything a
        /// player uses reaches the same funnel, including things whose use is not a gesture at all - a
        /// recipe is learned, a letter is read, and replicating either would have a body mime drinking
        /// while a window opens on the wrong machine. A subcategory the game adds later is ignored until
        /// somebody has decided what it looks like.
        /// </summary>
        private static readonly ItemSubCategory[] _replicatedUses =
        [
            ItemSubCategory.Consumable_Potion,
            ItemSubCategory.Consumable_Food,
            ItemSubCategory.Consumable_Plant,
            ItemSubCategory.Consumable_RawFood,
            ItemSubCategory.Consumable_Throw,
        ];

        /// <summary>
        /// Whether using this item is worth telling anyone about: an ability, or one of the things a
        /// player visibly consumes.
        /// </summary>
        private static bool IsWorthReplicating(Item item)
        {
            ItemCategory category = item.Category;
            if (category == null) return false;

            if (category.IsFlowAbility()) return true;

            ItemSubCategory sub = category.SubCategory;
            foreach (ItemSubCategory known in _replicatedUses)
            {
                if (sub == known) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether an entity is the local player. Null - an ability with no owner - is not.
        /// </summary>
        private static bool IsLocalPlayer(Entity entity)
        {
            if (entity == null) return false;
            if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return false;

            return player.Pointer == entity.Pointer;
        }

        private static bool IsLocalPlayer(Actor actor)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return false;

            // By pointer, for the usual reason: two proxies over one native object are different managed
            // instances, so the equality operator answers a question about destruction rather than about
            // identity.
            return player.Pointer == actor.Pointer;
        }
    }
}
