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
    /// Puts the status effects other players landed onto the local player.
    ///
    /// The far half of <see cref="StatusWatch"/>: an attacker's machine poisoned a body that has no
    /// character behind it and said so, and this is where that becomes a real effect on a real character
    /// - with its icon, its duration and its own game's rules about resisting it.
    ///
    /// Done from the frame loop rather than where the message arrives, for the reason everything here is:
    /// the game may only be touched from the frame loop, and an effect routinely arrives before there is
    /// a player to put it on.
    /// </summary>
    internal static class RemoteStatus
    {
        /// <summary>
        /// Applies everything that has arrived for the local player. Call once per frame from a loaded
        /// world.
        /// </summary>
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

        /// <summary>
        /// Puts one effect on the local player.
        /// </summary>
        private static void Apply(StatusEffectHandler handler, in StatusEffectMessage message)
        {
            try
            {
                SubDatabase_StatusEffect? database = ProviderAccess.StatusEffectDatabase;
                if (database == null) return;

                StatusEffect effect = database.GetStatusEffectByGuid(message.EffectId);
                if (effect == null)
                {
                    // A real possibility rather than a defensive check: the other player may be running a
                    // mod that adds effects this game does not have. Naming it is the whole value of the
                    // line, because the symptom otherwise is health draining with nothing to explain it.
                    MelonLogger.Warning($"[Status] {message.Source} applied '{message.EffectId}', which this game does not know. Mod sets may differ.");
                    return;
                }

                // The guard, so the watch on the other side does not read this as an effect this machine
                // decided to apply and report it straight back. It checks whether the body is a puppet as
                // well, and the local player is not one, so this is the second of two answers - which is
                // the point, because the first stops being true the moment anything else replicates onto
                // a body that is not a puppet.
                using (RemoteApply<StatusScope>.Enter())
                {
                    // Applied with no source info, so the effect is the game's own with the game's own
                    // duration and stacking. Naming the attacker would mean handing over a body from
                    // another machine, and there is nothing here that could be.
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
