using Drova_Modding_API;
using Drova_Modding_API.Systems.Morphing;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Players.Patches;
using Il2CppDrova;
using Il2CppDrova.HitSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Keeps the local player's morph creature from hitting teammates while friendly fire is off.
    /// The creature keeps its kind's alignment, so it counts every human body as an enemy.
    /// </summary>
    internal sealed class MorphFriendlyFire : ICoopSystem
    {
        /// <summary>Blows of the morph creature on a teammate that were dropped.</summary>
        internal static int Refused { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(HitFilter_Alignment), nameof(HitFilter_Alignment.Check),
                typeof(MorphFriendlyFirePatches), nameof(MorphFriendlyFirePatches.CheckPrefix));
        }

        void ICoopSystem.EndSession()
        {
            Refused = 0;
        }

        /// <summary>Drops a blow of the local morph creature on a teammate the rules protect.</summary>
        internal static bool CheckPrefix(HitFilter_Alignment filter, HitTriggerArgs args, ref bool result)
        {
            Actor? body = MorphManager.Body;
            if (body == null)
            {
                return true;
            }

            try
            {
                // Melee hits carry the attacker as the projectile source, an ability's hit as the entity.
                Entity? attacker = args.ProjectileEntitySource;
                if (attacker == null && args.ABI != null)
                {
                    attacker = args.Entity;
                }

                if (attacker == null || attacker.Pointer != body.Pointer || !RemotePlayers.TryFindOwner(filter.GetEntity(), out PlayerId teammate)
                    || Arena.AllowsHarm(teammate))
                {
                    return true;
                }

                Refused++;
                result = false;
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Morph] checking a morph creature's blow on a teammate failed: " + e);
                return true;
            }
        }
    }
}
