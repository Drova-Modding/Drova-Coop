using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Morphing;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Sends the local player's morph creature and weapon set on change and to new arrivals.
    /// <see cref="RemotePlayers"/> swaps a morphed teammate's body for that creature.
    /// </summary>
    internal sealed class MorphBroadcaster : ICoopSystem
    {
        private const float CheckInterval = 0.25f;

        private static readonly Dictionary<PlayerId, (string Guid, sbyte WeaponSet)> CreatureByPlayer = new();

        private static string _lastSent = string.Empty;
        private static sbyte _lastSentSet = -1;
        private static float _sinceLastCheck;
        private static int _lastRosterCount;

        /// <summary>Creature guid and weapon set of each morphed teammate.</summary>
        internal static IReadOnlyDictionary<PlayerId, (string Guid, sbyte WeaponSet)> Creatures => CreatureByPlayer;

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Reset();
                return;
            }

            _sinceLastCheck += unscaledDeltaTime;
            if (_sinceLastCheck < CheckInterval) return;
            _sinceLastCheck = 0f;

            string current = MorphManager.IsMorphed ? MorphManager.BodyGuid ?? string.Empty : string.Empty;
            sbyte set = (sbyte)(current.Length > 0 ? MorphManager.WeaponSet : -1);

            // Somebody arriving never heard of a morph that began before they came.
            bool rosterChanged = CoopSession.Players.Count != _lastRosterCount;
            _lastRosterCount = CoopSession.Players.Count;
            if (!rosterChanged && current == _lastSent && set == _lastSentSet) return;

            _lastSent = current;
            _lastSentSet = set;
            NetworkAccess.SendToAll(new MorphMessage { PlayerId = CoopSession.LocalPlayerId, CreatureGuid = current, WeaponSet = set });
            MelonLogger.Msg(current.Length > 0 ? $"[Morph] {CoopSession.LocalPlayerId} sent: morphed into {current}, weapon set {set}." : $"[Morph] {CoopSession.LocalPlayerId} sent: not morphed.");
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<MorphMessage>(MessageIds.Morph, OnMorph);
        }

        void ICoopSystem.EndSession()
        {
            CreatureByPlayer.Clear();
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            CreatureByPlayer.Remove(player);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            Reset();
        }

        private static void Reset()
        {
            _lastSent = string.Empty;
            _lastSentSet = -1;
            _sinceLastCheck = 0f;
            _lastRosterCount = 0;
        }

        private static void OnMorph(INetPeer peer, MorphMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                message.PlayerId = sender;
                CoopSession.SendToClientsExcept(message, peer.Id);
                Remember(sender, message.CreatureGuid, message.WeaponSet);
                return;
            }

            if (!CoopSession.IsFromHost()) return;
            if (message.PlayerId == CoopSession.LocalPlayerId) return;

            Remember(message.PlayerId, message.CreatureGuid, message.WeaponSet);
        }

        private static void Remember(PlayerId player, string guid, sbyte weaponSet)
        {
            if (string.IsNullOrEmpty(guid)) CreatureByPlayer.Remove(player);
            else CreatureByPlayer[player] = (guid, weaponSet);
        }
    }
}
