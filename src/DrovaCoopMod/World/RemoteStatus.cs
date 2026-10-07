using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.StatusEffects;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Puts the status effects other players landed onto the local player. The receiving half of <see cref="StatusWatch"/>.
    /// </summary>
    internal static class RemoteStatus
    {
        /// <summary>Applies everything that has arrived for the local player. Call once per frame.</summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            StatusEffectHandler handler = player.GetStatusEffectHandler();
            if (handler == null) return;

            while (CoopSession.TryTakeStatusEffect(out StatusEffectMessage message))
            {
                Apply(handler, message);
            }
        }

        private static void Apply(StatusEffectHandler handler, in StatusEffectMessage message)
        {
            try
            {
                SubDatabase_StatusEffect? database = ProviderAccess.StatusEffectDatabase;
                if (database == null) return;

                StatusEffect effect = database.GetStatusEffectByGuid(message.EffectId);
                if (effect == null)
                {
                    // The other player may run a mod that adds effects this game does not have.
                    MelonLogger.Warning($"[Status] {message.Source} applied '{message.EffectId}', which this game does not know. Mod sets may differ.");
                    return;
                }

                // So StatusWatch does not report this effect straight back.
                using (RemoteApply<StatusScope>.Enter())
                {
                    // No source info: the attacker's body lives on another machine.
                    handler.AddEffectFromSource(effect);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Status] applying a reported effect failed: " + e);
            }
        }
    }
}
