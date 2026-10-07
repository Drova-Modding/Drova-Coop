using System.Globalization;
using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One swing at a resource spot, replayed by each machine's vanilla code rather than replicated as state.
    /// Carries the absolute charge count afterwards and receivers only step down to it, so it is idempotent.
    /// </summary>
    public struct ResourceWorkMessage : INetMessage
    {
        /// <summary>
        /// The longest save-root instance key accepted (real keys are 36 or 72 characters).
        /// </summary>
        public const int MaxKeyLength = 80;

        /// <summary>The swing was a player's, so the receiver replays it as its own player's.</summary>
        public const byte FlagByPlayer = 1;

        /// <summary>The worked spot, by its save root's instance key.</summary>
        public string RootKey;

        /// <summary>
        /// How many charges the spot has left. An apply only ever steps down to this.
        /// </summary>
        public byte ChargesAfter;

        /// <summary>What the sender held before, so a divergence shows in the log.</summary>
        public byte ChargesBefore;

        /// <summary>See <see cref="FlagByPlayer"/>.</summary>
        public byte Flags;

        /// <summary>Who swung. Stamped by the host when it relays.</summary>
        public byte Player;

        /// <summary>The sender's own count of reported swings, for the log only.</summary>
        public uint Sequence;

        /// <summary>
        /// Marks a position key. No save key can begin with it, so the two kinds never mix.
        /// </summary>
        public const char PositionPrefix = '@';

        /// <summary>
        /// Names a spot by its authored position, for spots with no saveable component.
        /// Rounded to whole units so float noise cannot disagree between machines.
        /// </summary>
        public static string MakePositionKey(int x, int y)
        {
            return PositionPrefix + x.ToString(CultureInfo.InvariantCulture) + ","
                + y.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Whether a key is a well-formed position key. See <see cref="MakePositionKey"/>.
        /// </summary>
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

            // The key is compared as text, so a length bound is enough.
            if (end - at > 10) return false;

            for (int index = at; index < end; index++)
            {
                if (text[index] < '0' || text[index] > '9') return false;
            }

            return true;
        }

        /// <summary>
        /// Whether a save-root key may cross. Runtime roots number themselves "0", "1"... and would alias
        /// unrelated objects on the far machine, so a guid shape is required.
        /// </summary>
        public static bool IsReplicableKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

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
    /// Host to a joining client: every resource spot's charges, read from the savegame since only streamed chunks are live.
    /// </summary>
    public struct ResourceSpotSnapshotMessage : INetMessage
    {
        /// <summary>How many spots ride in one message.</summary>
        public const int MaxRecords = 14;

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
