using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.Talent;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Sends the local player's learned talents when they change, and to anybody who arrives.
    /// Talents shape combos and swing timing, which the game reads from the swinging body.
    /// </summary>
    internal sealed class TalentBroadcaster : ICoopSystem
    {
        private const float CheckInterval = 1f;

        private static string[] _lastSent = [];
        private static float _sinceLastCheck;
        private static int _lastRosterCount;

        private static readonly Dictionary<PlayerId, (string[] Guids, int Version)> TalentsByPlayer = new();
        private static readonly Dictionary<PlayerId, string[]> TalentParts = new();

        /// <summary>Each other player's learned talents, with a version that counts their changes.</summary>
        internal static IReadOnlyDictionary<PlayerId, (string[] Guids, int Version)> Talents => TalentsByPlayer;

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

            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            try
            {
                TalentActorModule? talents = player.TalentActorModule?.TryCast<TalentActorModule>();
                if (talents == null || talents._learnedTalents == null) return;

                List<string> current = new();
                foreach (string guid in talents._learnedTalents) current.Add(guid);
                current.Sort(StringComparer.Ordinal);

                // Somebody arriving never heard what this player learned before they came.
                bool rosterChanged = CoopSession.Players.Count != _lastRosterCount;
                _lastRosterCount = CoopSession.Players.Count;
                if (!rosterChanged && current.SequenceEqual(_lastSent)) return;

                _lastSent = current.ToArray();
                BroadcastTalents(_lastSent);
                MelonLogger.Msg($"[Talent] sent {_lastSent.Length} learned talents.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Talent] reading this player's talents failed: " + e);
            }
        }

        private static void Reset()
        {
            _lastSent = [];
            _sinceLastCheck = 0f;
            _lastRosterCount = 0;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<PlayerTalentsMessage>(MessageIds.PlayerTalents, OnTalents);
        }

        void ICoopSystem.EndSession()
        {
            TalentsByPlayer.Clear();
            TalentParts.Clear();
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            TalentsByPlayer.Remove(player);
            TalentParts.Remove(player);
        }

        /// <summary>Tells everyone which talents the local player has learned.</summary>
        private static void BroadcastTalents(string[] guids)
        {
            if (!CoopSession.IsLive) return;

            foreach (PlayerTalentsMessage part in new PlayerTalentsMessage { PlayerId = CoopSession.LocalPlayerId, Guids = guids }.Split())
            {
                NetworkAccess.SendToAll(part);
            }
        }

        private static void OnTalents(INetPeer peer, PlayerTalentsMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                // Relayed part by part: every receiver gathers the parts itself.
                message.PlayerId = sender;
                CoopSession.SendToClientsExcept(message, peer.Id);

                if (Gather(sender, ref message)) RememberTalents(sender, message.Guids);
                return;
            }

            if (!CoopSession.IsFromHost()) return;
            if (message.PlayerId == CoopSession.LocalPlayerId) return;

            if (Gather(message.PlayerId, ref message)) RememberTalents(message.PlayerId, message.Guids);
        }

        /// <returns>True when the message is now the whole list.</returns>
        private static bool Gather(PlayerId player, ref PlayerTalentsMessage message)
        {
            if (TalentParts.Remove(player, out string[] earlier)) message.Guids = [.. earlier, .. message.Guids];

            if (!message.More) return true;

            if (message.Guids.Length <= CoopSession.MaxGathered) TalentParts[player] = message.Guids;
            return false;
        }

        private static void RememberTalents(PlayerId player, string[] guids)
        {
            int version = TalentsByPlayer.TryGetValue(player, out (string[] Guids, int Version) existing) ? existing.Version + 1 : 1;
            TalentsByPlayer[player] = (guids, version);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
