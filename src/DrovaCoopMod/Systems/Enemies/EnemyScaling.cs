using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Applies the host's enemy health and damage rules by scaling the wound in <c>HealthChange.Intern_CalculateResultingValue</c>,
    /// so saved health stays vanilla. Brawls are skipped, and relayed creature blows are scaled in <see cref="ScaleRelayed"/>.
    /// </summary>
    internal sealed class EnemyScaling : ICoopSystem
    {
        string? ICoopSystem.HookSwitch => HookSwitches.Scaling;

        /// <summary>Whether the patch applied.</summary>
        internal static bool Hooked { get; private set; }

        /// <summary>Whether the patch has ever run, to tell an inlined target from a working one.</summary>
        internal static bool HookFired { get; private set; }

        /// <summary>How many blows this machine scaled. For <c>coop_status</c>.</summary>
        internal static int Scaled { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooked = Hooking.TryPostfix(harmony, typeof(HealthChange), nameof(HealthChange.Intern_CalculateResultingValue),
                typeof(EnemyScalingPatches), nameof(EnemyScalingPatches.CalculatePostfix));
        }

        /// <summary>Divides the wound a creature is about to take by the session's rule.</summary>
        internal static void CalculatePostfix(HealthChange __instance, Health defenderHealth, ref HealthChange.HealthChangeCalc __result)
        {
            try
            {
                HookFired = true;

                if (!CoopSession.SharesWorld) return;

                int damage = __result.ResultingDamage;
                if (damage >= 0) return;

                if (defenderHealth == null) return;

                // A creature's blow on this machine's own player. A puppet is left alone: its owner scales it.
                if (CoopSession.EnemyDamagePercent != CoopProtocol.DefaultEnemyDamagePercent
                    && defenderHealth.OwnerEntity?.TryCast<Actor>() is { } player && RemotePlayers.IsLocalPlayer(player)
                    && IsCreature(__instance.Source, out _))
                {
                    __result.ResultingDamage = -ScaleDamage(-damage);
                    Scaled++;
                    return;
                }

                ushort percent = CoopSession.EnemyHealthPercent;
                if (percent == CoopProtocol.DefaultEnemyHealthPercent) return;

                if (!IsCreature(defenderHealth.OwnerEntity, out Actor? defender)) return;
                if (defender.CustomStats != null && defender.CustomStats.IsInBrawl) return;

                if (IsCreature(__instance.Source, out _) || RemoteEnemyHealth.ApplyingCreatureBlow) return;

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

        /// <summary>A creature's blow on this player relayed by the host, which the funnel above cannot tell from any other.</summary>
        internal static int ScaleRelayed(int amount) => CoopSession.EnemyDamagePercent == CoopProtocol.DefaultEnemyDamagePercent ? amount : ScaleDamage(amount);

        // Rounded up, so a landed blow never drops to nothing.
        private static int ScaleDamage(int damage) => (int)Math.Ceiling(damage * (double)CoopSession.EnemyDamagePercent / CoopProtocol.DefaultEnemyDamagePercent);

        /// <summary>Whether an entity is an actor that is no player's body. Summons count as creatures.</summary>
        private static bool IsCreature(Entity? entity, [NotNullWhen(true)] out Actor? actor)
        {
            actor = null;

            if (entity == null) return false;

            Actor? candidate = entity.TryCast<Actor>();
            if (candidate == null) return false;

            if (RemotePlayers.IsPlayersBody(candidate)) return false;

            actor = candidate;
            return true;
        }
    }
}
