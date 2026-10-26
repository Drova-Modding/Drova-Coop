using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Morphing;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Sends which creature the local player is morphed into when it changes, and to anybody who arrives.
    /// <see cref="RemotePlayers"/> swaps a morphed teammate's body for that creature.
    /// </summary>
    internal sealed class MorphBroadcaster : ICoopSystem
    {
        private const float CheckInterval = 0.25f;

        private static readonly Dictionary<PlayerId, string> CreatureByPlayer = new();

        private static string _lastSent = string.Empty;
        private static float _sinceLastCheck;
        private static int _lastRosterCount;

        /// <summary>The creature guid each morphed teammate is, by player. Absent when not morphed.</summary>
        internal static IReadOnlyDictionary<PlayerId, string> Creatures => CreatureByPlayer;

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

            // Somebody arriving never heard of a morph that began before they came.
            bool rosterChanged = CoopSession.Players.Count != _lastRosterCount;
            _lastRosterCount = CoopSession.Players.Count;
            if (!rosterChanged && current == _lastSent) return;

            _lastSent = current;
            NetworkAccess.SendToAll(new MorphMessage { PlayerId = CoopSession.LocalPlayerId, CreatureGuid = current });
            MelonLogger.Msg(current.Length > 0 ? $"[Morph] {CoopSession.LocalPlayerId} sent: morphed into {current}." : $"[Morph] {CoopSession.LocalPlayerId} sent: not morphed.");
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
                Remember(sender, message.CreatureGuid);
                return;
            }

            if (!CoopSession.IsFromHost()) return;
            if (message.PlayerId == CoopSession.LocalPlayerId) return;

            Remember(message.PlayerId, message.CreatureGuid);
        }

        private static void Remember(PlayerId player, string guid)
        {
            if (string.IsNullOrEmpty(guid)) CreatureByPlayer.Remove(player);
            else CreatureByPlayer[player] = guid;
        }
    }
}
