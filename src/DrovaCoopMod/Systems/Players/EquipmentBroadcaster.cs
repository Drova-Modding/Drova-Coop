using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.Items;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Tells everyone what the local player has equipped, when it changes.
    /// Polled, so it also catches the set the game restores on a save load.
    /// </summary>
    internal sealed class EquipmentBroadcaster : ICoopSystem
    {
        // Fast enough that a mid-fight weapon-set swap arrives before the next swing.
        private const float ChecksPerSecond = 4f;

        private const float CheckInterval = 1f / ChecksPerSecond;

        private static readonly List<string> Current = new();
        private static readonly List<byte> CurrentSets = new();

        private static string[] _lastSent = [];
        private static byte[] _lastSentSets = [];
        private static byte _activeSet;
        private static byte _lastSentActiveSet;
        private static byte _bodyType;
        private static byte _voiceId;
        private static byte _lastSentBodyType;
        private static byte _lastSentVoiceId;
        private static float _sinceLastCheck;
        private static int _lastRosterCount;

        private static readonly Dictionary<PlayerId, EquipmentSnapshot> EquipmentByPlayer = new();

        // Lists that crossed in parts, held until the last part. Receivers replace what they knew, so acting
        // on each part would leave only the last one.
        private static readonly Dictionary<PlayerId, PlayerEquipmentMessage> EquipmentParts = new();

        /// <summary>Each other player's equipped item ids, versioned because applying equipment rebuilds the body's slots.</summary>
        internal static IReadOnlyDictionary<PlayerId, EquipmentSnapshot> Equipment => EquipmentByPlayer;

        /// <summary>Sends the local player's equipment when it has changed or somebody joined.</summary>
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
            if (!TryCollect(player)) return;

            // A newcomer has never been told what anyone is wearing.
            bool rosterChanged = CoopSession.Players.Count != _lastRosterCount;
            _lastRosterCount = CoopSession.Players.Count;

            if (!rosterChanged && Unchanged()) return;

            _lastSent = Current.ToArray();
            _lastSentSets = CurrentSets.ToArray();
            _lastSentActiveSet = _activeSet;
            _lastSentBodyType = _bodyType;
            _lastSentVoiceId = _voiceId;

            BroadcastEquipment(_lastSent, _lastSentSets, _activeSet, _bodyType, _voiceId);

            MelonLogger.Msg($"[Equip] sent build {_bodyType}, voice {_voiceId} and {_lastSent.Length} items: " +
                string.Join(", ", _lastSent));
        }

        /// <summary>Forgets what was last sent, so a new session gets it again.</summary>
        private static void Reset()
        {
            _lastSent = [];
            _lastSentSets = [];
            _sinceLastCheck = 0f;
            _lastRosterCount = 0;
        }

        // Every equipped item's readable id and its set, in both sets. False while the equipment module is not ready.
        private static bool TryCollect(Actor player)
        {
            try
            {
                Actor.EquipmentModule? equipment =player.GetEquipmentModule();
                if (equipment == null) return false;

                Current.Clear();
                CurrentSets.Clear();

                ActorBodyType? build =player.GetActorGender();
                _bodyType = build != null ? (byte)build.GetBodyType() : (byte)0;
                _voiceId = build != null ? (byte)build.GetVoiceId() : (byte)0;

                _activeSet = (byte)equipment.ActiveSetIndex;

                // Every set, not only the one in hand: the ammo slot belongs to both.
                foreach (ActorEquipSlot slot in equipment.AllItemSlotsRef)
                {
                    if (slot == null || slot.IsDummy) continue;

                    Item? item = slot.GetItemOnSlot();
                    if (item == null) continue;

                    // The game fills an empty slot with a placeholder item such as bare hands.
                    Item? placeholder =equipment.GetEmptyItemForSlot(slot.GetSlotType());
                    if (placeholder != null && placeholder.Pointer == item.Pointer) continue;

                    string id = item.ReadableId;
                    if (string.IsNullOrEmpty(id)) continue;

                    Current.Add(id);

                    // -1 means the slot belongs to every set.
                    int set = slot.SetIndex;
                    CurrentSets.Add(set < 0 ? PlayerEquipmentMessage.SharedSet : (byte)set);
                }

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Equip] could not read the local player's equipment: " + e.Message);
                return false;
            }
        }

        private static bool Unchanged()
        {
            if (_bodyType != _lastSentBodyType || _voiceId != _lastSentVoiceId) return false;

            if (_activeSet != _lastSentActiveSet) return false;

            return Current.SequenceEqual(_lastSent) && CurrentSets.SequenceEqual(_lastSentSets);
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<PlayerEquipmentMessage>(MessageIds.PlayerEquipment, OnEquipment);
        }

        void ICoopSystem.EndSession()
        {
            EquipmentByPlayer.Clear();
            EquipmentParts.Clear();
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            EquipmentByPlayer.Remove(player);
            EquipmentParts.Remove(player);
        }

        /// <summary>Sends the local player's equipment. Reliable, since it is sent only on change and a lost one stays wrong.</summary>
        private static void BroadcastEquipment(string[] itemIds, byte[] itemSets, byte activeSet, byte bodyType, byte voiceId)
        {
            if (!CoopSession.IsLive) return;

            PlayerEquipmentMessage message = new()
            {
                PlayerId = CoopSession.LocalPlayerId,
                ItemIds = itemIds,
                ItemSets = itemSets,
                ActiveSet = activeSet,
                BodyType = bodyType,
                VoiceId = voiceId,
            };

            foreach (PlayerEquipmentMessage part in message.Split()) NetworkAccess.SendToAll(part);
        }

        private static void OnEquipment(INetPeer peer, PlayerEquipmentMessage message)
        {
            if (NetworkAccess.Role == NetRole.Host)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                // Relayed part by part: every receiver gathers the parts itself.
                message.PlayerId = sender;
                CoopSession.SendToClientsExcept(message, peer.Id);

                if (Gather(sender, ref message)) Remember(sender, message);
                return;
            }

            if (!CoopSession.IsFromHost()) return;
            if (message.PlayerId == CoopSession.LocalPlayerId) return;

            if (Gather(message.PlayerId, ref message)) Remember(message.PlayerId, message);
        }

        /// <returns>True when the message is now the whole outfit.</returns>
        private static bool Gather(PlayerId player, ref PlayerEquipmentMessage message)
        {
            if (EquipmentParts.Remove(player, out PlayerEquipmentMessage earlier))
            {
                message.ItemIds = [.. earlier.ItemIds, .. message.ItemIds];
                message.ItemSets = [.. earlier.ItemSets, .. message.ItemSets];
            }

            if (!message.More) return true;

            if (message.ItemIds.Length <= CoopSession.MaxGathered) EquipmentParts[player] = message;
            return false;
        }

        // Versioned so a body knows it has something new to put on.
        private static void Remember(PlayerId player, in PlayerEquipmentMessage message)
        {
            int version = EquipmentByPlayer.TryGetValue(player, out EquipmentSnapshot existing) ? existing.Version + 1 : 1;
            EquipmentByPlayer[player] = new EquipmentSnapshot(
                message.ItemIds ?? [],
                message.ItemSets ?? [],
                message.ActiveSet,
                message.BodyType,
                message.VoiceId,
                version);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
