using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One global variable changing value, so quest and world state converge across the session.
    ///
    /// The variable is named by its owning list's guid and its id within that list, which is how the
    /// game itself resolves one, so both machines land on the same authored asset. What crosses is the
    /// new value alone. The write is applied through the game's own <c>SetValue</c>, so every quest
    /// listener reacts on the far side exactly as it did at home.
    ///
    /// Any peer may send one. The host applies it and passes it on to everybody else, so two clients
    /// converge without knowing about each other.
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

        /// <summary>
        /// Which player asserted this, so sequences from two senders are never compared with each
        /// other. Filled in by the host when it relays, and by the sender for its own traffic.
        /// </summary>
        public byte Origin;

        /// <summary>
        /// The sender's own count of assertions, stamped before any listener ran.
        ///
        /// This is what makes causal order recoverable. The write hook sits on <c>SetValue</c>, and
        /// <c>SetValue</c> runs its whole listener cascade inside itself - so a listener that writes a
        /// second variable completes, hook and all, before the outer hook returns. Reporting from the
        /// postfix therefore puts every effect on the wire ahead of its own cause. Taking the stamp in
        /// the prefix records the order the game actually wrote them in, and the receiver applies by
        /// stamp rather than by arrival.
        /// </summary>
        public uint Sequence;

        /// <summary>
        /// What the sender's world held before this write, for every kind but float.
        ///
        /// Decides nothing. It is carried so that the receiver can say, in one log line at the moment
        /// it happens, that the two worlds had already disagreed about this variable - a fact that
        /// previously could only be recovered by diffing two savegames after the fact.
        /// </summary>
        public int PreviousIntValue;

        /// <summary>What the sender's world held before this write, when the kind is float.</summary>
        public float PreviousFloatValue;

        /// <summary>The longest operation identity carried: a graph name and a node uid.</summary>
        public const int MaxOperationLength = 128;

        /// <summary>
        /// For a relative change a quest graph's node made (SetGIntNode Add): which node, as "graph/uid" - the same on
        /// every machine. The receiver's own copy of that graph skips the node when it gets there, so the party counts
        /// once. Empty otherwise.
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
            ListGuid = Truncate(reader.GetString() ?? string.Empty, MaxGuidLength);
            VarId = Truncate(reader.GetString() ?? string.Empty, MaxIdLength);
            Kind = reader.GetByte();
            IntValue = reader.GetInt();
            FloatValue = reader.GetFloat();
            Origin = reader.GetByte();
            Sequence = reader.GetUInt();
            PreviousIntValue = reader.GetInt();
            PreviousFloatValue = reader.GetFloat();
            Operation = Truncate(reader.GetString() ?? string.Empty, MaxOperationLength);
        }

        /// <summary>
        /// A value of the given kind as a log line shows it. A quest state is shown by number, because
        /// this assembly cannot see the game's enum.
        /// </summary>
        /// <param name="kind">Which of the four kinds the value is.</param>
        /// <param name="intValue">The value for every kind but float.</param>
        /// <param name="floatValue">The value when the kind is float.</param>
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
