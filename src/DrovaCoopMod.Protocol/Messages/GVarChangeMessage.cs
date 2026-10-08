using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One global variable changing value, applied through the game's own <c>SetValue</c> so quest listeners react.
    /// Any peer may send one, and the host relays it to everybody else.
    /// </summary>
    public struct GVarChangeMessage : INetMessage
    {
        /// <summary>The longest list guid accepted.</summary>
        public const int MaxGuidLength = 36;

        /// <summary>The longest variable id accepted.</summary>
        public const int MaxIdLength = 64;

        /// <summary>The value is a bool, carried in <see cref="IntValue"/> as zero or one.</summary>
        public const byte KindBool = 0;

        /// <summary>The value is an int, carried in <see cref="IntValue"/>.</summary>
        public const byte KindInt = 1;

        /// <summary>The value is a float, carried in <see cref="FloatValue"/>.</summary>
        public const byte KindFloat = 2;

        /// <summary>The value is a quest state, carried in <see cref="IntValue"/> as the enum's number.</summary>
        public const byte KindQuest = 3;

        /// <summary>The longest operation identity carried: a graph name and a node uid.</summary>
        private const int MaxOperationLength = 128;

        /// <summary>The owning list's guid.</summary>
        public string ListGuid;

        /// <summary>The variable's id within its list.</summary>
        public string VarId;

        /// <summary>Which of the four kinds the value is.</summary>
        public byte Kind;

        /// <summary>The value for every kind but float.</summary>
        public int IntValue;

        /// <summary>The value when <see cref="Kind"/> is <see cref="KindFloat"/>.</summary>
        public float FloatValue;

        /// <summary>Which player asserted this, so sequences from two senders are never compared.</summary>
        public byte Origin;

        /// <summary>
        /// The sender's assertion count, stamped in the <c>SetValue</c> prefix before its listener cascade runs.
        /// The receiver applies by stamp, so a cause is never applied after its effects.
        /// </summary>
        public uint Sequence;

        /// <summary>What the sender's world held before this write, for every kind but float. Only logged.</summary>
        public int PreviousIntValue;

        /// <summary>What the sender's world held before this write, when the kind is float.</summary>
        public float PreviousFloatValue;

        /// <summary>
        /// For a relative SetGIntNode Add, the node as "graph/uid", so the receiver's graph skips it and the party counts once.
        /// Empty otherwise.
        /// </summary>
        public string Operation;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Truncate(ListGuid ?? string.Empty, MaxGuidLength));
            writer.Put(Truncate(VarId ?? string.Empty, MaxIdLength));
            writer.Put(Kind);
            writer.Put(IntValue);
            writer.Put(FloatValue);
            writer.Put(Origin);
            writer.Put(Sequence);
            writer.Put(PreviousIntValue);
            writer.Put(PreviousFloatValue);
            writer.Put(Truncate(Operation ?? string.Empty, MaxOperationLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            ListGuid = Truncate(reader.GetString(), MaxGuidLength);
            VarId = Truncate(reader.GetString(), MaxIdLength);
            Kind = reader.GetByte();
            IntValue = reader.GetInt();
            FloatValue = reader.GetFloat();
            Origin = reader.GetByte();
            Sequence = reader.GetUInt();
            PreviousIntValue = reader.GetInt();
            PreviousFloatValue = reader.GetFloat();
            Operation = Truncate(reader.GetString(), MaxOperationLength);
        }

        /// <summary>A value as a log line shows it. Quest states show as numbers, since this assembly cannot see the game's enum.</summary>
        public static string Describe(byte kind, int intValue, float floatValue)
        {
            return kind switch
            {
                KindBool => intValue != 0 ? "true" : "false",
                KindFloat => floatValue.ToString("0.###"),
                KindQuest => "quest state " + intValue,
                _ => intValue.ToString(),
            };
        }

        private static string Truncate(string value, int limit)
        {
            return value.Length <= limit ? value : value[..limit];
        }
    }
}
