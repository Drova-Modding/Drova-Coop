using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes creatures tougher for a party, by the host's rule.
    ///
    /// The rule is a percentage of authored health, and it is applied to the party's blows rather than
    /// to the creatures. <c>HealthChange.Intern_CalculateResultingValue</c> is where the game turns a
    /// blow into the wound a defender takes, after armor, resistances and blockers, and it hands that
    /// figure back by value to <c>ChangeHealth</c>, which writes it. Dividing the figure there is the
    /// same fight as a creature with that much more health, and it leaves every number the creature
    /// owns alone: its maximum, its current health, and whatever of either the savegame keeps.
    ///
    /// Only the party's blows. A creature fighting a creature is the game's own business, and a blow on
    /// a player or on a remote player's body is answered by the victim's own machine, where the rule has
    /// no bearing. A scripted brawl is left alone too: the game ends one by clamping the wound to a
    /// knockout, and a wound scaled after that clamp would leave the loser standing.
    ///
    /// Every machine runs this on the blows it resolves, and each blow is scaled exactly once. The host
    /// resolves the party's blows on the creatures it owns, a client resolves its own leased brawl, and
    /// a relayed blow crosses as the figure from before this point and is resolved only where it lands.
    /// </summary>
    internal static class EnemyScaling
    {
        /// <summary>Whether the patch applied. False means the rule is a number nothing acts on.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>
        /// Whether the patch has ever run. A patch that applies and never fires is what an inlined
        /// target looks like, and nothing else would say so.
        /// </summary>
        internal static bool HookFired { get; private set; }

        /// <summary>How many blows this machine scaled. For <c>coop_status</c>.</summary>
        internal static int Scaled { get; private set; }

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooked = Hooking.TryPostfix(harmony, typeof(HealthChange), nameof(HealthChange.Intern_CalculateResultingValue),
                typeof(EnemyScaling), nameof(CalculatePostfix));
        }

        /// <summary>
        /// Divides the wound a creature is about to take by the session's rule.
        ///
        /// Runs for every health change in the game, so the cheap refusals come first: no session, the
        /// rule at a hundred, or a change that heals. The result is a struct handed back by value, which
        /// is why it is taken by reference here rather than read off the change afterwards.
        /// </summary>
        private static void CalculatePostfix(HealthChange __instance, Health defenderHealth, ref HealthChange.HealthChangeCalc __result)
        {
            try
            {
                HookFired = true;

                if (!CoopSession.IsLive) return;

                ushort percent = CoopSession.EnemyHealthPercent;
                if (percent == CoopProtocol.DefaultEnemyHealthPercent) return;

                int damage = __result.ResultingDamage;
                if (damage >= 0) return;

                if (defenderHealth == null) return;
                if (!IsCreature(defenderHealth.OwnerEntity, out Actor defender)) return;
                if (defender.CustomStats != null && defender.CustomStats.IsInBrawl) return;

                if (__instance != null && IsCreature(__instance.Source, out _)) return;

                // Rounded up, so a blow that landed at all still lands one point. The game itself rounds
                // the wound up at this step, and a blow the rule turned into nothing would read as a
                // creature that cannot be hurt.
                int scaled = (int)Math.Ceiling(-damage * (double)CoopProtocol.DefaultEnemyHealthPercent / percent);

                __result.ResultingDamage = -scaled;
                Scaled++;
            }
            catch (Exception e)
            {
                // Swallowed, because this runs inside the game's own damage path. An exception let out
                // of here is a blow that never lands, on every machine, for the rest of the session.
                MelonLogger.Error("[Scale] scaling a blow failed: " + e);
            }
        }

        /// <summary>
        /// Whether an entity is a creature: an actor that is neither the player at this machine nor a
        /// remote player's body. A summon counts as a creature, so its blows and the blows on it stay
        /// the game's own.
        /// </summary>
        private static bool IsCreature(Entity? entity, out Actor actor)
        {
            actor = null!;

            if (entity == null) return false;

            Actor? candidate = entity.TryCast<Actor>();
            if (candidate == null) return false;

            if (RemotePlayers.IsPlayersBody(candidate)) return false;

            actor = candidate;
            return true;
        }
    }
}
