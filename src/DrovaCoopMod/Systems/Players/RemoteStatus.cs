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
        private static readonly Queue<StatusEffectMessage> IncomingEffects = new();
        private static readonly Dictionary<PlayerId, byte> LastStatusSequence = new();

        /// <summary>Per target, since the receiver's half-range window would read a shared counter's jumps as late.</summary>
        private static readonly Dictionary<PlayerId, byte> NextStatusSequence = new();

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive) return;
            if (IncomingEffects.Count == 0) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            StatusEffectHandler? handler = player.GetStatusEffectHandler();
            if (handler == null) return;

            while (TryTakeStatusEffect(out StatusEffectMessage message))
            {
                Apply(player, handler, message);
            }
        }

        private static void Apply(Actor player, StatusEffectHandler handler, in StatusEffectMessage message)
        {
            try
            {
                if (message.Origin == DamageReportMessage.OriginCreature && !RemoteDamage.IsCreatureNear(message.Creature, player)) return;

                SubDatabase_StatusEffect? database = ProviderAccess.StatusEffectDatabase;
                if (database == null) return;

                StatusEffect? effect = database.GetStatusEffectByGuid(message.EffectId);
                if (effect == null)
                {
                    MelonLogger.Warning($"[Status] {message.Source} applied '{message.EffectId}', which this game does not know. Mod sets may differ.");
                    return;
                }

                // So StatusWatch does not report this effect straight back.
                using (RemoteApply<StatusScope>.Enter())
                {
                    // No source info, since the attacker's body lives on another machine.
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

        /// <summary>Tells a player this machine put a status effect on their body.</summary>
        internal static void ReportStatusEffect(PlayerId target, string effectId,
            byte origin = DamageReportMessage.OriginPlayer, EnemyNetId? creature = null)
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
                Creature = creature ?? EnemyNetId.None,
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

        private static bool TryTakeStatusEffect(out StatusEffectMessage message)
            => IncomingEffects.TryDequeue(out message);

        private static void OnStatusEffect(INetPeer peer, StatusEffectMessage message)
        {
            if (!CoopSession.IsLive) return;
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                message.Source = sender;

                // The pair actually involved, as for a blow. Only the host's creatures report effects.
                if (message.Origin == DamageReportMessage.OriginCreature) return;
                if (!Arena.AllowsHarm(sender, message.Target)) return;

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
