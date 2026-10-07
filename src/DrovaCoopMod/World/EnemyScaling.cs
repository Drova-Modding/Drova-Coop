using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes creatures tougher for a party by dividing the party's blows by the host's rule.
    /// Scaling the wound in <c>HealthChange.Intern_CalculateResultingValue</c> leaves the creature's saved health alone.
    /// Brawls are skipped, since the game clamps their wound to a knockout before this point.
    /// </summary>
    internal static class EnemyScaling
    {
        /// <summary>Whether the patch applied.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>Whether the patch has ever run, to tell an inlined target from a working one.</summary>
        internal static bool HookFired { get; private set; }

        /// <summary>How many blows this machine scaled. For <c>coop_status</c>.</summary>
        internal static int Scaled { get; private set; }

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooked = Hooking.TryPostfix(harmony, typeof(HealthChange), nameof(HealthChange.Intern_CalculateResultingValue),
                typeof(EnemyScaling), nameof(CalculatePostfix));
        }

        /// <summary>Divides the wound a creature is about to take by the session's rule.</summary>
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

                // Rounded up like the game does, so a landed blow still does one point.
                int scaled = (int)Math.Ceiling(-damage * (double)CoopProtocol.DefaultEnemyHealthPercent / percent);

                __result.ResultingDamage = -scaled;
                Scaled++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Scale] scaling a blow failed: " + e);
            }
        }

        /// <summary>Whether an entity is an actor that is no player's body. Summons count as creatures.</summary>
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
