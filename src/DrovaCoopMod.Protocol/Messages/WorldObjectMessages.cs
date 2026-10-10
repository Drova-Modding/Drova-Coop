using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One scene object's saved state list (bools, floats or child activation flags), sent whole.
    /// Both machines build the list from the same scene content, so indices agree. Client to host, host to the rest.
    /// </summary>
    public struct WorldObjectMessage : INetMessage
    {
        /// <summary>The longest root key carried.</summary>
        public const int MaxKeyLength = 72;

        /// <summary>The most entries one list may hold. Longer lists are refused, since truncating shifts indices.</summary>
        public const int MaxValues = 32;

        /// <summary>A list of bool states, one per <c>ISaveableBool</c> under the root.</summary>
        public const byte KindBool = 0;

        /// <summary>A list of float states, one per <c>ISaveableFloat</c> under the root.</summary>
        public const byte KindFloat = 1;

        /// <summary>A list of child activation flags, as the root's activation saveable holds them.</summary>
        public const byte KindActivation = 2;

        /// <summary>An Alraune (mandragora) spot: values [pulled, caught], each 0 or 1.</summary>
        public const byte KindAlraune = 3;

        /// <summary>An elevator: values [the state it is at or heading to].</summary>
        public const byte KindElevator = 4;

        /// <summary>Sprite-swap breakables (spider eggs): one flag per entry, which only ever turns on.</summary>
        public const byte KindSprite = 5;

        /// <summary>The highest kind this build knows.</summary>
        public const byte LastKind = KindSprite;

        /// <summary>The save root key.</summary>
        public string RootKey;

        /// <summary>Which list this is.</summary>
        public byte Kind;

        /// <summary>Who changed it. Set by the host from the connection.</summary>
        public byte Player;

        /// <summary>The whole list. Bools and flags travel as zero or one.</summary>
        public float[] Values;

        /// <summary>Whether the key, kind and length are valid to apply.</summary>
        public readonly bool IsReplicable()
        {
            if (!ResourceWorkMessage.IsReplicableKey(RootKey)) return false;
            if (Kind > LastKind) return false;

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
    /// Host to a joining client: every world object whose state the session has seen change. Chunked, and
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
                    Kind = kind > WorldObjectMessage.LastKind ? WorldObjectMessage.KindBool : kind,
                    Values = WorldObjectMessage.ReadValues(reader),
                };
            }
        }
    }

    /// <summary>Host to one client: the world-state snapshot is complete.</summary>
    public struct WorldObjectSnapshotEndMessage : INetMessage
    {
        /// <summary>How many entries were sent.</summary>
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
