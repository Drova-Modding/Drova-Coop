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
    ///
    /// Polled rather than hooked, for the reason everything in this mod is polled: a patch on a small
    /// method in this build can apply, report success and never run. Equipment changes are rare and
    /// player-driven, so a poll costs a walk over about a dozen slots a few times a second and nothing
    /// else - and unlike a hook, it cannot miss a change it was not told about, such as the set the game
    /// restores when a save is loaded.
    /// </summary>
    internal static class EquipmentBroadcaster
    {
        /// <summary>
        /// How often the local player's gear is looked at. Fast enough that swapping weapon sets mid-fight
        /// reaches the other player before the next swing, slow enough that it is not doing this every
        /// frame for a thing that changes a handful of times an hour.
        /// </summary>
        private const float ChecksPerSecond = 4f;

        private const float CheckInterval = 1f / ChecksPerSecond;

        // Reused so a check that finds nothing new allocates nothing.
        private static readonly List<string> _current = new();
        private static readonly List<byte> _currentSets = new();

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

        /// <summary>
        /// Sends the local player's equipment when it has changed since the last time it was sent.
        /// </summary>
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

            // Somebody arriving is the other reason to send. They were not in the session when this last
            // changed, so nothing has ever told them what anyone is wearing, and equipment that only goes
            // out on change would leave every player who joined late looking at empty hands until their
            // friend happened to swap a weapon.
            bool rosterChanged = CoopSession.Players.Count != _lastRosterCount;
            _lastRosterCount = CoopSession.Players.Count;

            if (!rosterChanged && Unchanged()) return;

            _lastSent = _current.ToArray();
            _lastSentSets = _currentSets.ToArray();
            _lastSentActiveSet = _activeSet;
            _lastSentBodyType = _bodyType;
            _lastSentVoiceId = _voiceId;

            // Said out loud rather than left to the wire to swallow. Truncation is not a degraded outfit -
            // the far side compares against the truncated list too, so the dropped pieces are never noticed
            // as missing and never resent, and the body wears them for the rest of the session.
            if (_current.Count > PlayerEquipmentMessage.MaxItems)
            {
                MelonLogger.Warning($"[Equip] this player has {_current.Count} items equipped but only " +
                    $"{PlayerEquipmentMessage.MaxItems} fit on the wire; the rest will not appear on other screens.");
            }

            CoopSession.BroadcastEquipment(_lastSent, _lastSentSets, _activeSet, _bodyType, _voiceId);

            // The ids themselves, not just how many. Whether a weapon reached the other machine at all is
            // the first fork in diagnosing a body that is not holding one, and a count cannot answer it.
            MelonLogger.Msg($"[Equip] sent build {_bodyType}, voice {_voiceId} and {_lastSent.Length} items: " +
                string.Join(", ", _lastSent));
        }

        /// <summary>
        /// Forgets what was last sent, so rejoining sends it again rather than assuming the new session
        /// already knows.
        /// </summary>
        internal static void Reset()
        {
            _lastSent = [];
            _lastSentSets = [];
            _sinceLastCheck = 0f;
            _lastRosterCount = 0;
        }

        /// <summary>
        /// Gathers the readable ids of everything the player has equipped, in both weapon sets, along with
        /// which set each one sits in.
        ///
        /// Walked slot by slot rather than through the collect-into-a-list overload: that one takes its
        /// list by reference, and handing an Il2Cpp collection across that boundary every quarter second
        /// buys nothing here.
        ///
        /// Empty slots are skipped, and they are not simply null: the game fills an unused slot with a
        /// placeholder item - the bare hands are one - so a set is never actually empty. Sending those
        /// would have the far side dutifully equip a pair of fists over the fists it already has.
        /// </summary>
        /// <returns>False when the equipment module is not ready, which is normal for a frame or two
        /// after a world loads.</returns>
        private static bool TryCollect(Actor player)
        {
            try
            {
                Actor.EquipmentModule equipment = player.GetEquipmentModule();
                if (equipment == null) return false;

                _current.Clear();
                _currentSets.Clear();

                // The build and voice live on their own component rather than in a slot, because they are
                // what the player is rather than what they are wearing.
                ActorBodyType build = player.GetActorGender();
                _bodyType = build != null ? (byte)build.GetBodyType() : (byte)0;
                _voiceId = build != null ? (byte)build.GetVoiceId() : (byte)0;

                _activeSet = (byte)equipment.ActiveSetIndex;

                // Every set, not only the one in hand. A player carries two, and the slot holding their
                // ammo belongs to both - so collecting the active set alone sent the arrows and left the
                // bow behind, which reached the other machine as ammo for a weapon nobody had given it.
                foreach (ActorEquipSlot slot in equipment.AllItemSlotsRef)
                {
                    if (slot == null || slot.IsDummy) continue;

                    Item item = slot.GetItemOnSlot();
                    if (item == null) continue;

                    // The placeholder for this slot type, compared by pointer because these are proxies
                    // over native objects and two proxies for one object are different managed instances.
                    Item placeholder = equipment.GetEmptyItemForSlot(slot.GetSlotType());
                    if (placeholder != null && placeholder.Pointer == item.Pointer) continue;

                    string id = item.ReadableId;
                    if (string.IsNullOrEmpty(id)) continue;

                    _current.Add(id);

                    // The game says -1 for a slot that belongs to every set, which does not fit the byte
                    // this rides in.
                    int set = slot.SetIndex;
                    _currentSets.Add(set < 0 ? PlayerEquipmentMessage.SharedSet : (byte)set);
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

            // The set in hand, because swapping sets changes nothing about which items are equipped and
            // everything about which of them the body is holding.
            if (_activeSet != _lastSentActiveSet) return false;

            if (_current.Count != _lastSent.Length) return false;

            for (int index = 0; index < _lastSent.Length; index++)
            {
                if (!string.Equals(_current[index], _lastSent[index], StringComparison.Ordinal)) return false;
                if (index >= _lastSentSets.Length || _currentSets[index] != _lastSentSets[index]) return false;
            }

            return true;
        }
    }
}
