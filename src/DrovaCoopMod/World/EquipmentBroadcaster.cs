using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Items;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Tells everyone what the local player has equipped, when it changes.
    /// Polled, so it also catches the set the game restores on a save load.
    /// </summary>
    internal static class EquipmentBroadcaster
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

        /// <summary>Sends the local player's equipment when it has changed or somebody joined.</summary>
        internal static void Update(float unscaledDeltaTime)
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

            CoopSession.BroadcastEquipment(_lastSent, _lastSentSets, _activeSet, _bodyType, _voiceId);

            MelonLogger.Msg($"[Equip] sent build {_bodyType}, voice {_voiceId} and {_lastSent.Length} items: " +
                string.Join(", ", _lastSent));
        }

        /// <summary>Forgets what was last sent, so a new session gets it again.</summary>
        internal static void Reset()
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
                Actor.EquipmentModule equipment = player.GetEquipmentModule();
                if (equipment == null) return false;

                Current.Clear();
                CurrentSets.Clear();

                ActorBodyType build = player.GetActorGender();
                _bodyType = build != null ? (byte)build.GetBodyType() : (byte)0;
                _voiceId = build != null ? (byte)build.GetVoiceId() : (byte)0;

                _activeSet = (byte)equipment.ActiveSetIndex;

                // Every set, not only the one in hand: the ammo slot belongs to both.
                foreach (ActorEquipSlot slot in equipment.AllItemSlotsRef)
                {
                    if (slot == null || slot.IsDummy) continue;

                    Item item = slot.GetItemOnSlot();
                    if (item == null) continue;

                    // The game fills an empty slot with a placeholder item such as bare hands.
                    Item placeholder = equipment.GetEmptyItemForSlot(slot.GetSlotType());
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
    }
}
