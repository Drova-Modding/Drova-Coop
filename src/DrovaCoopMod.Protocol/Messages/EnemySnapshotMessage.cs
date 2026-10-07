using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Where the host's creatures are and what they do, batched and unreliable at fifteen per second.
    /// Death is latched here and also sent reliably elsewhere.
    /// </summary>
    public struct EnemySnapshotMessage : INetMessage
    {
        /// <summary>How many creatures fit in one message, so a full batch stays inside a datagram.</summary>
        public const int MaxRecords = 32;

        /// <summary>The unit positions are quantized to, in world units.</summary>
        public const float PositionStep = 0.25f;

        /// <summary>Nothing. What an absent action reads as.</summary>
        public const byte NoAction = 255;

        /// <summary>Nothing. What an absent interaction mode or target reads as.</summary>
        public const byte NoValue = 15;

        /// <summary>The creature is moving rather than standing.</summary>
        public const byte FlagMoving = 1 << 0;

        /// <summary>The creature is in combat.</summary>
        public const byte FlagInCombat = 1 << 3;

        /// <summary>The creature is knocked out.</summary>
        public const byte FlagKnockout = 1 << 4;

        /// <summary>
        /// The creature let go of its action. Still holding means charging, so without this the far copy never strikes.
        /// </summary>
        public const byte FlagActionReleased = 1 << 5;

        /// <summary>The creature holds a torch, which its routine hands it only where its AI runs.</summary>
        public const byte FlagTorch = 1 << 6;

        /// <summary>The creature is dead. Latched, never cleared.</summary>
        public const byte FlagDead = 1 << 7;

        /// <summary>One creature's state, positioned relative to the message's origin.</summary>
        public struct Record
        {
            /// <summary>Which creature.</summary>
            public EnemyNetId NetId;

            /// <summary>Position east of the message's origin, in quarter-units.</summary>
            public short OffsetX;

            /// <summary>Position north of the message's origin, in quarter-units.</summary>
            public short OffsetY;

            /// <summary>Which way it faces, as a 256th of a turn.</summary>
            public byte LookAngle;

            /// <summary>Which way it is moving, as a 256th of a turn.</summary>
            public byte MoveAngle;

            /// <summary>See the flag constants. Bits one and two carry the movement mode.</summary>
            public byte Flags;

            /// <summary>
            /// Low nibble: the interaction mode. High nibble: which player it is attacking, or
            /// <see cref="NoValue"/>.
            /// </summary>
            public byte ModeAndTarget;

            /// <summary>Which action it is performing, or <see cref="NoAction"/>.</summary>
            public byte Action;

            /// <summary>Counts up per action so the far side can tell a new swing. Wraps.</summary>
            public byte ActionSequence;

            /// <summary>
            /// FNV-1a hash of the action prefab's name, or zero. Binds come from AI-equipped natural weapons the
            /// AI-less far copy never equips, so the prefab name is what both machines share.
            /// </summary>
            public uint ActionHash;
        }

        /// <summary>What the offsets are measured from.</summary>
        public float OriginX;

        /// <summary>See <see cref="OriginX"/>.</summary>
        public float OriginY;

        /// <summary>The creatures in this batch, read into a reused buffer. Never null after a read.</summary>
        public Record[] Records;

        /// <summary>How many of <see cref="Records"/> are meaningful.</summary>
        public int Count;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(OriginX);
            writer.Put(OriginY);

            Record[] records = Records ?? [];
            int count = Count;

            if (count > records.Length) count = records.Length;
            if (count > MaxRecords) count = MaxRecords;

            writer.Put((byte)count);

            for (int index = 0; index < count; index++)
            {
                Record record = records[index];

                writer.Put(record.NetId.Value);
                writer.Put(record.OffsetX);
                writer.Put(record.OffsetY);
                writer.Put(record.LookAngle);
                writer.Put(record.MoveAngle);
                writer.Put(record.Flags);
                writer.Put(record.ModeAndTarget);
                writer.Put(record.Action);
                writer.Put(record.ActionSequence);
                writer.Put(record.ActionHash);
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            OriginX = reader.GetFloat();
            OriginY = reader.GetFloat();

            int count = reader.GetByte();
            if (count > MaxRecords) count = MaxRecords;

            // Grown once and kept, since the receiver reuses one struct.
            if (Records == null || Records.Length < MaxRecords)
            {
                Records = new Record[MaxRecords];
            }

            for (int index = 0; index < count; index++)
            {
                Records[index] = new Record
                {
                    NetId = new EnemyNetId(reader.GetUShort()),
                    OffsetX = reader.GetShort(),
                    OffsetY = reader.GetShort(),
                    LookAngle = reader.GetByte(),
                    MoveAngle = reader.GetByte(),
                    Flags = reader.GetByte(),
                    ModeAndTarget = reader.GetByte(),
                    Action = reader.GetByte(),
                    ActionSequence = reader.GetByte(),
                    ActionHash = reader.GetUInt(),
                };
            }

            Count = count;
        }

        /// <summary>Whether the origin is a finite, plausible coordinate. An implausible batch is dropped whole.</summary>
        public readonly bool IsPlausible()
        {
            return IsPlausible(OriginX) && IsPlausible(OriginY);
        }

        private static bool IsPlausible(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;

            return value >= -PlayerStateMessage.MaxCoordinate && value <= PlayerStateMessage.MaxCoordinate;
        }
    }
}
