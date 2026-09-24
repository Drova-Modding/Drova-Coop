using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One rubble pile leaving a player's world - picked up out of the ground, or dropped at the cart.
    ///
    /// The pile is named rather than counted, and that is the whole design. A fungible count would only
    /// ever replicate the DUMP, leaving the far player's copy of the pile lying there for the thirty to
    /// sixty seconds of the carry - which is exactly the duplicated labour this is meant to remove - and
    /// "remove any one of my own piles" would free a different rock face on each machine, which the
    /// mining replay then re-fills. Naming it costs nothing: the spawning spot already has a
    /// cross-machine key for the mining sync, and a spot cannot be worked while its pile lies there, so
    /// the charge count the swing left is a unique second component.
    ///
    /// Nothing here is a running total and nothing is a delta. Every message is a fact about one named
    /// pile and every apply is a set insertion, so redelivery, reordering and a peer that is behind are
    /// no-ops by construction - strictly stronger than a monotone cursor, because there is no ordering
    /// assumption at all. The one ordering subtlety, a dump arriving before its pickup, is closed by
    /// making the dump imply the pickup.
    ///
    /// The variable rides along on a dump because there is no authored path from a destroyed pile to
    /// the counter: the pile prefab carries no global-variable component at all, and the cart's
    /// subtraction is authored on the cart. So the mod is always the writer of that subtraction on the
    /// far machine, and the wire has to say which variable. It also makes the log line name it, which is
    /// what makes a stuck day diagnosable rather than merely visible.
    /// </summary>
    public struct HaulPileMessage : INetMessage
    {
        /// <summary>The pile was picked up out of the world.</summary>
        public const byte KindTaken = 1;

        /// <summary>The pile was dropped at a cart, and one unit of the tally went with it.</summary>
        public const byte KindDumped = 2;

        /// <summary>A charge count that never names a real pile, so it can never be replicated.</summary>
        public const byte SlotUnknown = 0xFF;

        /// <summary>The spawning spot, by its save root's instance key - the mining sync's own name for it.</summary>
        public string SpotKey;

        /// <summary>
        /// How many charges the spot had left after the swing that dropped this pile.
        ///
        /// The second half of the pile's name. It is unique per lying pile because a spot refuses to be
        /// worked while its pile is still there, so the pile lying at a spot is always the one from that
        /// spot's most recent swing.
        /// </summary>
        public byte Slot;

        /// <summary>See <see cref="KindTaken"/> and <see cref="KindDumped"/>.</summary>
        public byte Kind;

        /// <summary>The tally's owning list, on a dump. Empty otherwise.</summary>
        public string ListGuid;

        /// <summary>The tally within that list, on a dump. Empty otherwise.</summary>
        public string VarId;

        /// <summary>Who hauled it. Stamped by the host when it relays, like every other message.</summary>
        public byte Player;

        /// <summary>The sender's own count of reported piles. For the log only.</summary>
        public uint Sequence;

        /// <summary>
        /// The cross-machine name of one pile.
        /// </summary>
        /// <param name="spotKey">The spawning spot's instance key.</param>
        /// <param name="slot">The charge count the spawning swing left.</param>
        public static string MakeId(string spotKey, byte slot)
        {
            return (spotKey ?? string.Empty) + "#" + slot;
        }

        /// <summary>
        /// Whether a pile may be spoken about at all.
        ///
        /// Reuses the mining sync's key rule verbatim, which is not tidiness: a runtime-created object's
        /// save root numbers itself with a per-savegame counter, and those numbers ALIAS between
        /// machines - this world's object 2 is an unrelated object 2 over there. Removing the wrong
        /// object is the one identity failure that silently damages something the player was never near.
        /// </summary>
        /// <param name="spotKey">The spawning spot's instance key.</param>
        /// <param name="slot">The charge count the spawning swing left.</param>
        public static bool IsReplicablePile(string spotKey, byte slot)
        {
            return slot != SlotUnknown && ResourceWorkMessage.IsReplicableKey(spotKey);
        }

        /// <summary>
        /// Whether this message can be acted on. Checked on arrival, so a malformed one is refused
        /// once rather than half-applied.
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
    /// What one world may do about one pile, and the record of what it has already done.
    ///
    /// Kept here, away from every game type, because the rule it encodes is the whole safety argument of
    /// shared hauling and deserves to be tested without a game running.
    ///
    /// The rule: <b>a world subtracts at most one unit for a pile, ever, and only for a pile it itself
    /// produced.</b> Every pile a world produces is accompanied by exactly one authored addition - the
    /// spawn and the addition ride the same spot-worked event and their conditions are satisfied
    /// together - so the number of subtractions a world can be talked into is bounded above by the
    /// number of additions it made. The counter can therefore never be driven below what the game alone
    /// would have reached, which matters because the quest gate is an exact equality against zero and
    /// nothing in the authored world ever assigns that variable outright. No floor, no clamp, no cursor.
    ///
    /// The decisions are deliberately set operations rather than arithmetic. Set insertion is
    /// commutative and idempotent, so every interleaving of two players hauling - including both of them
    /// dumping their own copy of the same pile in the same instant - lands exactly one subtraction per
    /// world with no claim, no lease and no round trip.
    /// </summary>
    public sealed class HaulLedger
    {
        /// <summary>What a world should do about a pile it has been told something about.</summary>
        public enum Decision
        {
            /// <summary>Take the pile out of this world; it is one this world produced and still holds.</summary>
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

        /// <summary>Forgets the session. The tombstones go with it; see the note on <see cref="Tag"/>.</summary>
        public void Clear()
        {
            _tagged.Clear();
            _gone.Clear();
            _subbed.Clear();
            _takenByPeer.Clear();
        }

        /// <summary>
        /// Records that this world produced a pile.
        ///
        /// Kept for the whole session rather than only while the pile exists, because the tombstone is
        /// the entire answer to a savegame being reloaded mid-session: the reloading machine gets its
        /// piles back and hauls them again, and its teammate deduplicates the second dump against a
        /// record it never dropped.
        /// </summary>
        /// <param name="id">The pile's cross-machine name.</param>
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
        /// Piles a teammate took out of this world and never dumped - the one genuine way this design
        /// can leave a world above zero with nothing left to haul. Bounded at one per teammate, because
        /// a player can carry only one load at a time.
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
