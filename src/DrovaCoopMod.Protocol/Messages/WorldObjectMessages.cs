using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One scene object's saved state, as the game itself would write it into a savegame.
    ///
    /// The game keeps this kind of state in three list-shaped components hanging off a save root: a list
    /// of bools, a list of floats, and a list of child activation flags. Each list is rebuilt on both
    /// machines by the same sweep over the same authored scene content, so an index means the same thing
    /// in both worlds and the whole list can cross as one value.
    ///
    /// The list crosses whole rather than as the one entry that moved. The components themselves write
    /// every entry on every change for the same reason: which entry changed is not reported by the
    /// event, and a list of three is smaller than the bookkeeping needed to find out.
    ///
    /// Client to host, host to everyone else. The host stamps <see cref="Player"/> from the connection.
    /// </summary>
    public struct WorldObjectMessage : INetMessage
    {
        /// <summary>The longest root key carried. A save root key is a guid or two, never longer.</summary>
        public const int MaxKeyLength = 72;

        /// <summary>
        /// The most entries one list may hold and still cross.
        ///
        /// A list longer than this is refused rather than truncated, at both ends. Truncating would put
        /// the two worlds' indices out of step, which is worse than not replicating at all: every entry
        /// after the cut would be applied to the wrong object.
        /// </summary>
        public const int MaxValues = 32;

        /// <summary>A list of bool states, one per <c>ISaveableBool</c> under the root.</summary>
        public const byte KindBool = 0;

        /// <summary>A list of float states, one per <c>ISaveableFloat</c> under the root.</summary>
        public const byte KindFloat = 1;

        /// <summary>A list of child activation flags, as the root's activation saveable holds them.</summary>
        public const byte KindActivation = 2;

        /// <summary>An Alraune (mandragora) spot: values [pulled, caught], each 0 or 1.</summary>
        public const byte KindAlraune = 3;

        /// <summary>The save root key, which both worlds agree on.</summary>
        public string RootKey;

        /// <summary>Which of the three lists this is. Anything else is refused on read.</summary>
        public byte Kind;

        /// <summary>Who changed it. Set by the host from the connection, not trusted from the payload.</summary>
        public byte Player;

        /// <summary>
        /// The whole list. Bools and activation flags travel as zero or one, which a float carries
        /// exactly, so one array shape serves all three kinds.
        /// </summary>
        public float[] Values;

        /// <summary>
        /// Whether this names something a teammate could act on. A key no save root would produce, an
        /// unknown kind, or a list longer than the cap are all refused rather than half applied.
        /// </summary>
        public readonly bool IsReplicable()
        {
            if (!ResourceWorkMessage.IsReplicableKey(RootKey)) return false;
            if (Kind > KindAlraune) return false;

            return Values != null && Values.Length <= MaxValues;
        }


        internal static void WriteValues(INetWriter writer, float[]? values)
        {
            float[] list = values ?? [];
            int count = list.Length > MaxValues ? MaxValues : list.Length;

            writer.Put((byte)count);
            for (int index = 0; index < count; index++)
            {
                writer.Put(list[index]);
            }
        }

        internal static float[] ReadValues(INetReader reader)
        {
            int count = reader.GetByte();
            if (count > MaxValues) count = MaxValues;

            float[] list = new float[count];
            for (int index = 0; index < count; index++)
            {
                list[index] = reader.GetFloat();
            }

            return list;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(RootKey, WorldObjectMessage.MaxKeyLength));
            writer.Put(Kind);
            writer.Put(Player);
            WriteValues(writer, Values);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            RootKey = WireStrings.Cut(reader.GetString(MaxKeyLength), WorldObjectMessage.MaxKeyLength);
            Kind = reader.GetByte();
            Player = reader.GetByte();
            Values = ReadValues(reader);
        }
    }

    /// <summary>
    /// Host to one client on joining: every world object whose state the session has seen move, so a
    /// late joiner's doors, levers and broken crates match everyone else's. Chunked;
    /// <see cref="WorldObjectSnapshotEndMessage"/> closes it.
    /// </summary>
    public struct WorldObjectSnapshotMessage : INetMessage
    {
        /// <summary>The most entries in one chunk: at its limits a record is about 204 bytes, and a relay forwards 1170.</summary>
        public const int MaxRecords = 5;

        /// <summary>One object's state as it stands.</summary>
        public struct Record
        {
            /// <summary>The save root key.</summary>
            public string RootKey;

            /// <summary>Which list, as <see cref="WorldObjectMessage"/> defines them.</summary>
            public byte Kind;

            /// <summary>The whole list.</summary>
            public float[] Values;
        }

        /// <summary>The entries in this chunk.</summary>
        public Record[] Records;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            Record[] records = Records ?? [];
            int count = records.Length > MaxRecords ? MaxRecords : records.Length;

            writer.Put((byte)count);
            for (int index = 0; index < count; index++)
            {
                writer.Put(WireStrings.Cut(records[index].RootKey, WorldObjectMessage.MaxKeyLength));
                writer.Put(records[index].Kind);
                WorldObjectMessage.WriteValues(writer, records[index].Values);
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
                string key = WireStrings.Cut(reader.GetString(WorldObjectMessage.MaxKeyLength), WorldObjectMessage.MaxKeyLength);
                byte kind = reader.GetByte();

                Records[index] = new Record
                {
                    RootKey = key,
                    Kind = kind > WorldObjectMessage.KindAlraune ? WorldObjectMessage.KindBool : kind,
                    Values = WorldObjectMessage.ReadValues(reader),
                };
            }
        }
    }

    /// <summary>
    /// Host to one client: the world-state snapshot is complete, and this is how many entries it held.
    /// </summary>
    public struct WorldObjectSnapshotEndMessage : INetMessage
    {
        /// <summary>How many entries were sent, so the receiver can say whether all arrived.</summary>
        public ushort Count;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Count);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Count = reader.GetUShort();
        }
    }
}
