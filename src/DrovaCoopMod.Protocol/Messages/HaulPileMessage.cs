using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One named rubble pile picked up or dumped at the cart. Every apply is a set insertion, so
    /// redelivery and reordering are no-ops, and a dump implies its pickup.
    /// </summary>
    public struct HaulPileMessage : INetMessage
    {
        /// <summary>The pile was picked up out of the world.</summary>
        public const byte KindTaken = 1;

        /// <summary>The pile was dropped at a cart, and one unit of the tally went with it.</summary>
        public const byte KindDumped = 2;

        /// <summary>A charge count that never names a real pile, so it can never be replicated.</summary>
        public const byte SlotUnknown = 0xFF;

        /// <summary>The spawning spot, by its save root's instance key.</summary>
        public string SpotKey;

        /// <summary>
        /// The spot's charges left after the dropping swing. Unique per lying pile, since a spot cannot be
        /// worked while its pile lies there.
        /// </summary>
        public byte Slot;

        /// <summary>See <see cref="KindTaken"/> and <see cref="KindDumped"/>.</summary>
        public byte Kind;

        /// <summary>The tally's owning list, on a dump. Empty otherwise.</summary>
        public string ListGuid;

        /// <summary>The tally within that list, on a dump. Empty otherwise.</summary>
        public string VarId;

        /// <summary>Who hauled it. Stamped by the host when it relays.</summary>
        public byte Player;

        /// <summary>The sender's own count of reported piles. For the log only.</summary>
        public uint Sequence;

        /// <summary>
        /// The cross-machine name of one pile.
        /// </summary>
        public static string MakeId(string spotKey, byte slot)
        {
            return (spotKey ?? string.Empty) + "#" + slot;
        }

        /// <summary>
        /// Whether a pile may be replicated, by the mining sync's key rule.
        /// </summary>
        public static bool IsReplicablePile(string spotKey, byte slot)
        {
            return slot != SlotUnknown && ResourceWorkMessage.IsReplicableKey(spotKey);
        }

        /// <summary>
        /// Whether this message can be acted on. Checked on arrival.
        /// </summary>
        public readonly bool IsWellFormed()
        {
            if (Kind != KindTaken && Kind != KindDumped) return false;
            if (!IsReplicablePile(SpotKey, Slot)) return false;

            if (Kind == KindDumped)
            {
                if (string.IsNullOrEmpty(ListGuid) || string.IsNullOrEmpty(VarId)) return false;
            }

            return true;
        }

        /// <summary>This pile's cross-machine name.</summary>
        public readonly string Id()
        {
            return MakeId(SpotKey, Slot);
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(SpotKey, ResourceWorkMessage.MaxKeyLength));
            writer.Put(Slot);
            writer.Put(Kind);
            writer.Put(WireStrings.Cut(ListGuid, GVarChangeMessage.MaxGuidLength));
            writer.Put(WireStrings.Cut(VarId, GVarChangeMessage.MaxIdLength));
            writer.Put(Player);
            writer.Put(Sequence);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            SpotKey = WireStrings.Cut(reader.GetString(), ResourceWorkMessage.MaxKeyLength);
            Slot = reader.GetByte();
            Kind = reader.GetByte();
            ListGuid = WireStrings.Cut(reader.GetString(), GVarChangeMessage.MaxGuidLength);
            VarId = WireStrings.Cut(reader.GetString(), GVarChangeMessage.MaxIdLength);
            Player = reader.GetByte();
            Sequence = reader.GetUInt();
        }
    }

    /// <summary>
    /// What one world may do about one pile. A world subtracts at most one unit per pile, and only for
    /// a pile it produced, so the tally never drops below what the game alone would reach.
    /// </summary>
    public sealed class HaulLedger
    {
        /// <summary>What a world should do about a pile it has been told something about.</summary>
        public enum Decision
        {
            /// <summary>Take the pile out of this world, which produced and still holds it.</summary>
            Remove,

            /// <summary>This world no longer holds that pile. Nothing to do.</summary>
            AlreadyRemoved,

            /// <summary>Subtract one unit of the tally.</summary>
            Sub,

            /// <summary>This world has already paid its one unit for that pile.</summary>
            DedupSub,

            /// <summary>This world never produced that pile, so it owes nothing for it.</summary>
            RefuseUntagged,
        }

        private readonly HashSet<string> _tagged = new(StringComparer.Ordinal);
        private readonly HashSet<string> _gone = new(StringComparer.Ordinal);
        private readonly HashSet<string> _subbed = new(StringComparer.Ordinal);
        private readonly Dictionary<byte, HashSet<string>> _takenByPeer = new();

        /// <summary>How many piles this world has produced this session.</summary>
        public int Tagged => _tagged.Count;

        /// <summary>How many units of tally this world has paid.</summary>
        public int Subbed => _subbed.Count;

        /// <summary>Forgets the session, tombstones included.</summary>
        public void Clear()
        {
            _tagged.Clear();
            _gone.Clear();
            _subbed.Clear();
            _takenByPeer.Clear();
        }

        /// <summary>
        /// Records that this world produced a pile. Kept all session so a teammate's mid-session reload
        /// cannot get the same pile paid twice.
        /// </summary>
        /// <returns>False when this world had already produced a pile by that name.</returns>
        public bool Tag(string id)
        {
            return _tagged.Add(id);
        }

        /// <summary>Whether this world ever produced that pile.</summary>
        public bool IsTagged(string id)
        {
            return _tagged.Contains(id);
        }

        /// <summary>Whether this world has already paid its unit for that pile.</summary>
        public bool HasSubbed(string id)
        {
            return _subbed.Contains(id);
        }

        /// <summary>
        /// What to do about a pile a teammate has taken out of their world.
        /// </summary>
        public Decision DecideRemoval(string id)
        {
            if (!_tagged.Contains(id)) return Decision.RefuseUntagged;
            if (_gone.Contains(id)) return Decision.AlreadyRemoved;

            return Decision.Remove;
        }

        /// <summary>
        /// What to do about the unit of tally that goes with a pile somebody has dumped.
        /// </summary>
        public Decision DecideSub(string id)
        {
            if (!_tagged.Contains(id)) return Decision.RefuseUntagged;
            if (_subbed.Contains(id)) return Decision.DedupSub;

            return Decision.Sub;
        }

        /// <summary>Records that this world no longer holds that pile, however it lost it.</summary>
        public void RecordRemoval(string id)
        {
            _gone.Add(id);
        }

        /// <summary>Records that this world has paid its one unit for that pile.</summary>
        public void RecordSub(string id)
        {
            _subbed.Add(id);
        }

        /// <summary>Remembers that a teammate is carrying a pile this world has given up.</summary>
        public void RecordTakenByPeer(byte player, string id)
        {
            if (!_takenByPeer.TryGetValue(player, out HashSet<string> ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                _takenByPeer[player] = ids;
            }

            ids.Add(id);
        }

        /// <summary>Forgets that, because the pile has been dumped and the unit has arrived.</summary>
        public void ClearTakenByPeer(string id)
        {
            foreach (KeyValuePair<byte, HashSet<string>> pair in _takenByPeer)
            {
                pair.Value.Remove(id);
            }
        }

        /// <summary>
        /// Piles a teammate took out of this world and never dumped, at most one per teammate.
        /// </summary>
        /// <param name="player">Only that teammate's, or every teammate's when null.</param>
        public IEnumerable<string> Orphans(byte? player = null)
        {
            foreach (KeyValuePair<byte, HashSet<string>> pair in _takenByPeer)
            {
                if (player.HasValue && pair.Key != player.Value) continue;

                foreach (string id in pair.Value)
                {
                    if (_subbed.Contains(id)) continue;

                    yield return id;
                }
            }
        }

        /// <summary>How many piles teammates hold that this world has not been paid for.</summary>
        public int OrphanCount()
        {
            int count = 0;

            foreach (string _ in Orphans())
            {
                count++;
            }

            return count;
        }
    }
}
