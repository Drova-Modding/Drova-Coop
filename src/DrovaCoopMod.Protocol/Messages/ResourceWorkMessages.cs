using System.Globalization;
using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One swing at a resource spot - a rock face, an ore vein, a herb, a tree.
    ///
    /// What crosses is the WORK, not the resulting state, and that choice is the whole design. A
    /// worked spot has consequences scattered across half a dozen authored components: the charge
    /// count, the erosion sprite, a spawned rubble pile with its own destroy callback, a quest
    /// counter, a deactivated node, particles. Replicating the state would mean replicating each of
    /// those and getting every one right; replicating the swing lets each machine derive all of them
    /// from its own vanilla code, which is already correct by construction.
    ///
    /// The value on the wire is the ABSOLUTE charge count afterwards, never a delta, and the receiver
    /// only ever steps its own spot DOWN to it. Charges never rise except on a savegame load, so that
    /// makes the message idempotent and commutative: a redelivery, an out-of-order arrival and a
    /// message from a machine that is behind are all no-ops, and two players swinging at the same
    /// rock in the same second converge with no claim, no lease and no round trip.
    /// </summary>
    public struct ResourceWorkMessage : INetMessage
    {
        /// <summary>
        /// The longest save-root instance key accepted.
        ///
        /// A real save's keys are a 36-character guid, or 72 where a root appends a custom key of its
        /// own. Eighty leaves headroom without letting a stranger choose how much memory to allocate.
        /// </summary>
        public const int MaxKeyLength = 80;

        /// <summary>The swing was a player's, so the receiver replays it as its own player's.</summary>
        public const byte FlagByPlayer = 1;

        /// <summary>The worked spot, by its save root's instance key.</summary>
        public string RootKey;

        /// <summary>
        /// How many charges the spot has left. The cursor: an apply only ever steps down to this.
        /// </summary>
        public byte ChargesAfter;

        /// <summary>What the sender held before. Carried so a divergence names itself in the log.</summary>
        public byte ChargesBefore;

        /// <summary>See <see cref="FlagByPlayer"/>.</summary>
        public byte Flags;

        /// <summary>Who swung. Stamped by the host when it relays, like every other message.</summary>
        public byte Player;

        /// <summary>The sender's own count of reported swings. For the log only.</summary>
        public uint Sequence;

        /// <summary>
        /// Whether a save-root instance key may be spoken about at all.
        ///
        /// Pure data, so it is unit-testable without a game running, and enforced on both send and
        /// apply. Two of the three refusals are safety rather than tidiness:
        ///
        /// A root whose guid component is missing returns the empty string and logs an error, so an
        /// empty key is a broken object rather than a valid one.
        ///
        /// A runtime-created object's root numbers itself with a per-savegame counter - "0", "1",
        /// "2". Those do not merely fail to match on the far machine, they ALIAS: this world's
        /// object 2 is some unrelated object 2 over there. That is the one identity failure that
        /// silently corrupts something a player was not even near, so the shape is required to look
        /// like a guid rather than merely to be non-empty.
        /// </summary>
        /// <summary>
        /// Marks a key that names a spot by where it stands rather than by its savegame identity.
        ///
        /// Chosen because no save key can begin with it, so the two kinds can never be confused for
        /// one another and an old peer refuses these outright instead of misreading one as a guid.
        /// </summary>
        public const char PositionPrefix = '@';

        /// <summary>
        /// Names a spot by its position, for spots the save system never named.
        ///
        /// Some authored resource spots carry no saveable component at all, because the quest counts
        /// their work through a global variable and never needs to store them one by one. Those spots
        /// have no identity to send, and the whole of mining and hauling is addressed by identity.
        ///
        /// Their position serves instead, and only for them. It is authored map data, identical in
        /// every copy of the game, and it is not minted at runtime, which is what makes it safe where
        /// a dynamic root's counter is not: two machines reading the same scene compute the same
        /// number, and a spot that does not exist over there matches nothing rather than matching
        /// something else. Rounded to whole units so the last bits of a float cannot disagree; rock
        /// faces stand many units apart, so rounding cannot merge two of them.
        /// </summary>
        /// <param name="x">The spot's world x, rounded to a whole unit.</param>
        /// <param name="y">The spot's world y, rounded to a whole unit.</param>
        public static string MakePositionKey(int x, int y)
        {
            return PositionPrefix + x.ToString(CultureInfo.InvariantCulture) + ","
                + y.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Whether a key is a well-formed position key. See <see cref="MakePositionKey"/>.
        /// </summary>
        /// <param name="key">Any key.</param>
        public static bool IsPositionKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key[0] != PositionPrefix) return false;

            int comma = key.IndexOf(',');
            if (comma <= 1 || comma == key.Length - 1) return false;

            return IsWholeNumber(key, 1, comma) && IsWholeNumber(key, comma + 1, key.Length);
        }

        private static bool IsWholeNumber(string text, int start, int end)
        {
            int at = start;
            if (at < end && text[at] == '-') at++;

            if (at >= end) return false;

            // A bound rather than a parse, because the value is never read back as a number: the key is
            // compared as text. What matters is that nothing but a number can be written here.
            if (end - at > 10) return false;

            for (int index = at; index < end; index++)
            {
                if (text[index] < '0' || text[index] > '9') return false;
            }

            return true;
        }

        /// <param name="key">A save root's instance key.</param>
        public static bool IsReplicableKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            // The player's own save root, which is never a resource spot and is per-machine by nature.
            if (key.StartsWith("Player#", StringComparison.Ordinal)) return false;

            if (key[0] == PositionPrefix) return IsPositionKey(key);

            if (key.Length != 36 && key.Length != 72) return false;

            foreach (char c in key)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex && c != '-') return false;
            }

            return true;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(RootKey, MaxKeyLength));
            writer.Put(ChargesAfter);
            writer.Put(ChargesBefore);
            writer.Put(Flags);
            writer.Put(Player);
            writer.Put(Sequence);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            RootKey = WireStrings.Cut(reader.GetString(), MaxKeyLength);
            ChargesAfter = reader.GetByte();
            ChargesBefore = reader.GetByte();
            Flags = reader.GetByte();
            Player = reader.GetByte();
            Sequence = reader.GetUInt();
        }
    }

    /// <summary>
    /// Host to one client on joining: how far every resource spot in this world has been worked.
    ///
    /// Read out of the host's savegame rather than off its live objects, and that is not an
    /// optimization but the only complete answer. A save root exists only once its chunk has
    /// streamed, so a walk of live objects would send exactly the spots around the host's own player
    /// - which is the set the joining player is least likely to be missing. The savegame holds every
    /// spot the host has ever visited, with no loading at all.
    /// </summary>
    public struct ResourceSpotSnapshotMessage : INetMessage
    {
        /// <summary>How many spots ride in one message. Matches the gvar snapshot's chunking.</summary>
        public const int MaxRecords = 16;

        /// <summary>One spot's charge count.</summary>
        public struct Record
        {
            /// <summary>The spot, by its save root's instance key.</summary>
            public string RootKey;

            /// <summary>How many charges it has left.</summary>
            public byte Charges;
        }

        /// <summary>The spots in this chunk.</summary>
        public Record[] Records;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            int count = Records == null ? 0 : Math.Min(Records.Length, MaxRecords);
            writer.Put((byte)count);

            for (int index = 0; index < count; index++)
            {
                writer.Put(WireStrings.Cut(Records![index].RootKey, ResourceWorkMessage.MaxKeyLength));

                writer.Put(Records[index].Charges);
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            int count = reader.GetByte();
            if (count > MaxRecords) count = MaxRecords;

            Records = new Record[count];

            for (int index = 0; index < count; index++)
            {
                Records[index] = new Record
                {
                    RootKey = WireStrings.Cut(reader.GetString(), ResourceWorkMessage.MaxKeyLength),
                    Charges = reader.GetByte(),
                };
            }
        }
    }

    /// <summary>
    /// Host to one client: that was every resource spot.
    ///
    /// Same reason the gvar catch-up has a terminator: a count that arrives explicitly is the only
    /// thing that can tell an empty queue from a per-frame budget that ran out.
    /// </summary>
    public struct ResourceSpotSnapshotEndMessage : INetMessage
    {
        /// <summary>How many spot records the host believes it sent.</summary>
        public uint RecordCount;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(RecordCount);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            RecordCount = reader.GetUInt();
        }
    }
}
