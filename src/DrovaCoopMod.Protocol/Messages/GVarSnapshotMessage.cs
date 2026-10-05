using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One list's worth of global variables that differ from their authored initial values, sent to a
    /// player who just joined.
    ///
    /// The catch-up half of <see cref="GVarChangeMessage"/>. Live changes only reach machines that are
    /// connected when they happen, so quest state the host earned before a client arrived would stay
    /// unsaid forever. At join the host walks its database and sends everything that has moved, and the
    /// client applies it through the same path a live change takes.
    ///
    /// Chunked per list, because the list guid would otherwise repeat on every record.
    /// </summary>
    public struct GVarSnapshotMessage : INetMessage
    {
        /// <summary>One variable that has moved from its initial value.</summary>
        public struct Record
        {
            /// <summary>The variable's id within its list.</summary>
            public string VarId;

            /// <summary>Which kind the value is. The kinds are <see cref="GVarChangeMessage"/>'s.</summary>
            public byte Kind;

            /// <summary>The value for every kind but float.</summary>
            public int IntValue;

            /// <summary>The value when the kind is <see cref="GVarChangeMessage.KindFloat"/>.</summary>
            public float FloatValue;
        }

        /// <summary>The longest list guid accepted.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>The longest variable id accepted.</summary>
        public const int MaxIdLength = 64;

        /// <summary>
        /// How many records fit in one chunk. Sized so a full chunk stays well inside a datagram even
        /// after the relay's framing and encryption.
        /// </summary>
        public const int MaxRecords = 14;

        /// <summary>The owning list's guid.</summary>
        public string ListGuid;

        /// <summary>The moved variables. Never null after a read.</summary>
        public Record[] Records;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(ListGuid, MaxGuidLength));

            Record[] records = Records ?? [];
            int count = records.Length <= MaxRecords ? records.Length : MaxRecords;

            writer.Put((byte)count);

            for (int index = 0; index < count; index++)
            {
                writer.Put(WireStrings.Cut(records[index].VarId, MaxIdLength));
                writer.Put(records[index].Kind);
                writer.Put(records[index].IntValue);
                writer.Put(records[index].FloatValue);
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            ListGuid = WireStrings.Cut(reader.GetString(), MaxGuidLength);

            // Bounded on read as well as on write. The count decides how much this machine allocates,
            // so it is not a number the sender gets to choose freely.
            int count = reader.GetByte();
            if (count > MaxRecords) count = MaxRecords;

            Records = new Record[count];

            for (int index = 0; index < count; index++)
            {
                Records[index] = new Record
                {
                    VarId = WireStrings.Cut(reader.GetString(), MaxIdLength),
                    Kind = reader.GetByte(),
                    IntValue = reader.GetInt(),
                    FloatValue = reader.GetFloat(),
                };
            }
        }
    }
}
