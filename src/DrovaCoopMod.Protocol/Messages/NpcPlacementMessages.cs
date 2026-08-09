using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Client to host: where does your world have these actors?
    ///
    /// Quest staging places NPCs, and staging the machines do not share - a story sequence one world
    /// finished and the other never starts - leaves the same actor standing in two different places.
    /// The client asks about authored actors near its player, and the host answers from its save
    /// without loading anything, the way the world-state question already works.
    /// </summary>
    public struct NpcPlacementRequestMessage : INetMessage
    {
        /// <summary>How many guids one request carries.</summary>
        public const int MaxGuids = 16;

        /// <summary>The longest guid accepted. Drova's actor guids are 36-character strings.</summary>
        public const int MaxGuidLength = 36;

        /// <summary>The actors asked about.</summary>
        public string[] Guids;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            int count = Guids == null ? 0 : System.Math.Min(Guids.Length, MaxGuids);

            writer.Put((byte)count);

            for (int index = 0; index < count; index++)
            {
                writer.Put(Truncate(Guids![index] ?? string.Empty));
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            int count = System.Math.Min((int)reader.GetByte(), MaxGuids);

            Guids = new string[count];

            for (int index = 0; index < count; index++)
            {
                Guids[index] = Truncate(reader.GetString() ?? string.Empty);
            }
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxGuidLength ? value : value[..MaxGuidLength];
        }
    }

    /// <summary>
    /// Host to one client: where these actors stand in the world everybody follows.
    ///
    /// Every asked guid is answered, unknown ones included - silence is indistinguishable from packet
    /// loss and would keep the question open forever.
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

                writer.Put(Truncate(record.Guid ?? string.Empty));
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
                    Guid = Truncate(reader.GetString() ?? string.Empty),
                    X = reader.GetFloat(),
                    Y = reader.GetFloat(),
                    Flags = reader.GetByte(),
                };
            }
        }

        private static string Truncate(string value)
        {
            return value.Length <= NpcPlacementRequestMessage.MaxGuidLength
                ? value
                : value[..NpcPlacementRequestMessage.MaxGuidLength];
        }
    }
}
