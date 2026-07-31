using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Where the creatures the host drives are, and what they are doing.
    ///
    /// Batched, and it has to be. One message per creature would cost more in framing than in content:
    /// a datagram carries about thirty bytes of relay envelope and another thirty of network header before
    /// anything of ours, so twenty creatures at fifteen updates a second would spend most of the link on
    /// addresses. Packed into one message they cost twelve bytes each.
    ///
    /// Unreliable, like the player stream and for the same reason. Every record is superseded fifteen
    /// times a second; a resend would arrive after the truth had already moved on. The one thing here that
    /// is not superseded is death, which is why the flag for it is latched and also travels reliably
    /// elsewhere - a position can be wrong for a frame, a creature standing up again cannot.
    ///
    /// Why any of this rather than letting each machine run its own copy: the creatures pick who
    /// attacks and who leads with their own random numbers, on whichever machine is running them. Two
    /// copies diverge within a second of a fight starting. Mirrored simulation cannot be corrected, only
    /// replaced.
    /// </summary>
    public struct EnemySnapshotMessage : INetMessage
    {
        /// <summary>
        /// How many creatures fit in one message. Sized so a full batch stays inside a datagram after the
        /// relay's framing and encryption, with margin.
        /// </summary>
        public const int MaxRecords = 32;

        /// <summary>
        /// The unit positions are quantized to. A quarter of a world unit is finer than a creature can
        /// move between two updates at a run, so the rounding is never what a player notices.
        /// </summary>
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
        /// The creature is dead. Latched: once set for a creature it is never cleared, because a corpse
        /// standing back up is the one thing this stream must never be able to say.
        /// </summary>
        public const byte FlagDead = 1 << 7;

        /// <summary>
        /// One creature's state, twelve bytes.
        ///
        /// Positions are relative to the message's origin so they fit in two bytes apiece; sending them
        /// absolute would need four each and buy nothing, since everything in one batch is near the same
        /// place by construction.
        /// </summary>
        public struct Record
        {
            /// <summary>Which creature.</summary>
            public EnemyNetId NetId;

            /// <summary>Position east of the message's origin, in quarter-units.</summary>
            public short OffsetX;

            /// <summary>Position north of the message's origin, in quarter-units.</summary>
            public short OffsetY;

            /// <summary>
            /// Which way it faces, as a 256th of a turn. Facing changes faster than anything else here and
            /// drives which way the sprite points, so it is worth a byte of its own.
            /// </summary>
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

            /// <summary>
            /// Counts up per action so the far side can tell a new swing from the one it is already
            /// playing. Wraps, and the comparison accounts for that.
            /// </summary>
            public byte ActionSequence;
        }

        /// <summary>
        /// What the offsets are measured from. Sent once per message rather than per record.
        /// </summary>
        public float OriginX;

        /// <summary>See <see cref="OriginX"/>.</summary>
        public float OriginY;

        /// <summary>
        /// The creatures in this batch. Never null after a read.
        ///
        /// Read into a reusable buffer. This arrives fifteen times a second, and allocating an array
        /// per message would hand the collector a steady stream of garbage for no reason.
        /// </summary>
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
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            OriginX = reader.GetFloat();
            OriginY = reader.GetFloat();

            int count = reader.GetByte();
            if (count > MaxRecords) count = MaxRecords;

            // Grown once and kept. The buffer belongs to the message struct's storage rather than to a
            // caller, so a message read into a fresh struct still allocates - but the receiver reuses one
            // struct, which is what makes this matter.
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
                };
            }

            Count = count;
        }

        /// <summary>
        /// Whether this message describes somewhere a creature could actually be.
        ///
        /// Refused whole rather than repaired. It is unreliable and replaced fifteen times a second,
        /// so dropping one costs nothing, while a repaired one would place every creature in the batch
        /// somewhere invented.
        /// </summary>
        /// <returns>False when the origin is not a finite, plausible coordinate.</returns>
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
