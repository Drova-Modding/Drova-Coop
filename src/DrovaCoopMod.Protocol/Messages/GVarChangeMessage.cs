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

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Truncate(ListGuid ?? string.Empty, MaxGuidLength));
            writer.Put(Truncate(VarId ?? string.Empty, MaxIdLength));
            writer.Put(Kind);
            writer.Put(IntValue);
            writer.Put(FloatValue);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            ListGuid = Truncate(reader.GetString() ?? string.Empty, MaxGuidLength);
            VarId = Truncate(reader.GetString() ?? string.Empty, MaxIdLength);
            Kind = reader.GetByte();
            IntValue = reader.GetInt();
            FloatValue = reader.GetFloat();
        }

        private static string Truncate(string value, int limit)
        {
            return value.Length <= limit ? value : value[..limit];
        }
    }
}
