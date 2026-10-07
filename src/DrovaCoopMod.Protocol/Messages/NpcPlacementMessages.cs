using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Client to host: where does your world have these actors? Quest staging can place the same NPC differently per machine.
    /// </summary>
    public struct NpcPlacementRequestMessage : INetMessage
    {
        /// <summary>How many guids one request carries.</summary>
        public const int MaxGuids = WireStrings.MaxGuids;

        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>The actors asked about.</summary>
        public string[] Guids;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            WireStrings.PutStrings(writer, Guids, MaxGuids, MaxGuidLength);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Guids = WireStrings.GetStrings(reader, MaxGuids, MaxGuidLength);
        }
    }

    /// <summary>
    /// Host to one client: where these actors stand. Every asked guid is answered, unknown ones included, so no question stays open.
    /// </summary>
    public struct NpcPlacementAnswerMessage : INetMessage
    {
        /// <summary>How many records one answer carries.</summary>
        public const int MaxRecords = NpcPlacementRequestMessage.MaxGuids;

        /// <summary>The host's world knows this actor.</summary>
        public const byte FlagKnown = 1;

        /// <summary>The host's world has this actor dead.</summary>
        public const byte FlagDead = 2;

        /// <summary>One actor's placement.</summary>
        public struct Record
        {
            /// <summary>Which actor.</summary>
            public string Guid;

            /// <summary>Where it stands, in world units.</summary>
            public float X;

            /// <summary>Where it stands, in world units.</summary>
            public float Y;

            /// <summary>See the flag constants.</summary>
            public byte Flags;
        }

        /// <summary>The placements, one per asked guid.</summary>
        public Record[] Records;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            int count = Records == null ? 0 : System.Math.Min(Records.Length, MaxRecords);

            writer.Put((byte)count);

            for (int index = 0; index < count; index++)
            {
                Record record = Records![index];

                writer.Put(WireStrings.Cut(record.Guid, NpcPlacementRequestMessage.MaxGuidLength));
                writer.Put(record.X);
                writer.Put(record.Y);
                writer.Put(record.Flags);
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            int count = System.Math.Min((int)reader.GetByte(), MaxRecords);

            Records = new Record[count];

            for (int index = 0; index < count; index++)
            {
                Records[index] = new Record
                {
                    Guid = WireStrings.Cut(reader.GetString(), NpcPlacementRequestMessage.MaxGuidLength),
                    X = reader.GetFloat(),
                    Y = reader.GetFloat(),
                    Flags = reader.GetByte(),
                };
            }
        }
    }
}
