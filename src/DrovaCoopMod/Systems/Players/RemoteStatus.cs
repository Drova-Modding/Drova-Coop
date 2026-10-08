using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.StatusEffects;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Puts the status effects other players landed onto the local player. The receiving half of <see cref="StatusWatch"/>.
    /// </summary>
    internal sealed class RemoteStatus : ICoopSystem
    {
        // Applying an effect twice is a second stack of poison, not a harmless repeat.
        private static readonly Queue<StatusEffectMessage> IncomingEffects = new();
        private static readonly Dictionary<PlayerId, byte> LastStatusSequence = new();

        // Per target: the receiver's half-range window would read a shared counter's jumps as late and drop them.
        private static readonly Dictionary<PlayerId, byte> NextStatusSequence = new();

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            StatusEffectHandler? handler = player.GetStatusEffectHandler();
            if (handler == null) return;

            while (TryTakeStatusEffect(out StatusEffectMessage message))
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

                StatusEffect? effect = database.GetStatusEffectByGuid(message.EffectId);
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

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<StatusEffectMessage>(MessageIds.StatusEffect, OnStatusEffect);
        }

        void ICoopSystem.EndSession()
        {
            IncomingEffects.Clear();
            LastStatusSequence.Clear();
            NextStatusSequence.Clear();
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            LastStatusSequence.Remove(player);
            NextStatusSequence.Remove(player);
        }

        /// <summary>Tells a player this machine put a status effect on their body. Reliable, sent once per application.</summary>
        internal static void ReportStatusEffect(PlayerId target, string effectId,
            byte origin = DamageReportMessage.OriginPlayer)
        {
            if (!CoopSession.IsLive) return;

            // Friendly fire applies to player effects, never to a creature's.
            if (origin != DamageReportMessage.OriginCreature && !Arena.AllowsHarm(target)) return;

            if (!target.IsAssigned || target == CoopSession.LocalPlayerId) return;
            if (string.IsNullOrEmpty(effectId)) return;

            StatusEffectMessage message = new()
            {
                Target = target,
                Source = CoopSession.LocalPlayerId,
                EffectId = effectId,
                Sequence = NextStatusSequenceFor(target),
                Origin = origin,
            };

            if (NetworkAccess.Role == NetRole.Host)
            {
                CoopSession.TrySendToPlayer(message, target);
                return;
            }

            NetworkAccess.SendToAll(message);
        }

        private static byte NextStatusSequenceFor(PlayerId target)
        {
            byte next = NextStatusSequence.TryGetValue(target, out byte last) ? unchecked((byte)(last + 1)) : (byte)1;
            NextStatusSequence[target] = next;

            return next;
        }

        /// <summary>Takes the next status effect that has arrived for the local player, oldest first.</summary>
        private static bool TryTakeStatusEffect(out StatusEffectMessage message)
            => IncomingEffects.TryDequeue(out message);

        private static void OnStatusEffect(INetPeer peer, StatusEffectMessage message)
        {
            if (!CoopSession.IsLive) return;
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                message.Source = sender;

                // The pair actually involved, as for a blow.
                if (message.Origin != DamageReportMessage.OriginCreature && !Arena.AllowsHarm(sender, message.Target)) return;

                if (message.Target == sender) return;

                if (message.Target == CoopSession.LocalPlayerId)
                {
                    EnqueueEffect(message);
                    return;
                }

                if (!CoopSession.Players.ContainsKey(message.Target)) return;

                CoopSession.TrySendToPlayer(message, message.Target);
                return;
            }

            if (!CoopSession.IsFromHost()) return;
            if (message.Target != CoopSession.LocalPlayerId) return;
            if (message.Origin != DamageReportMessage.OriginCreature && !Arena.AllowsHarm(message.Source)) return;

            EnqueueEffect(message);
        }

        private static void EnqueueEffect(in StatusEffectMessage message)
        {
            // A replayed packet must be refused, since a second application is a second stack.
            if (LastStatusSequence.TryGetValue(message.Source, out byte last)
                && !PlayerStateMessage.IsNewerAction(message.Sequence, last))
            {
                return;
            }

            LastStatusSequence[message.Source] = message.Sequence;

            CoopSession.EnqueueLatest(IncomingEffects, message, CoopSession.MaxPendingDamage);
        }
    }
}
