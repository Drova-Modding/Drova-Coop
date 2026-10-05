using System.Collections;
using System.Reflection;
using System.Text;
using Drova_Modding_API.Systems.Networking;
using Xunit;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// Every message, at its worst, fits what a relay forwards. A relay drops a bigger packet without a word to anybody,
    /// so a message over the limit simply never arrives - the first trade lost a whole bag that way.
    ///
    /// Filled mechanically: every string far past any cap - human text in a three-byte UTF-8 character, the game's own
    /// names in ASCII - and every array far past any count. Each Write cuts both to its own limits, so what is measured is the largest the message can be. Sized the way
    /// LiteNetLib writes it (a ushort length before each string's UTF-8 bytes), not the test writer's way.
    /// </summary>
    public class WorstCaseSizeTests
    {
        /// <summary>
        /// The relay's 1200 bytes, less its routing byte and an encrypted session's 28-byte seal, less the one-byte
        /// message id: what a message's own fields may take (LiteTransport.MaxPayloadSize).
        /// </summary>
        private const int MaxMessageBytes = 1200 - 1 - 28 - 1;

        public static IEnumerable<object[]> Messages() => WireFidelityTests.Messages();

        [Theory]
        [MemberData(nameof(Messages))]
        public void AtItsLimits_FitsWhatARelayForwards(Type messageType)
        {
            INetMessage message = (INetMessage)Fill(messageType, 0)!;

            SizeWriter size = new();
            message.Write(size);

            Assert.True(size.Bytes <= MaxMessageBytes, $"{messageType.Name}: {size.Bytes} bytes at its limits, {MaxMessageBytes} fit");
        }

        /// <summary>Fields that carry what a person wrote or reads, in any script: three UTF-8 bytes a character at worst.</summary>
        private static readonly HashSet<string> _humanText = new(StringComparer.Ordinal) { "PlayerName", "Name", "Text" };

        private static object? Fill(Type type, int depth, string name = "")
        {
            // Ids, guids, keys and asset paths are the game's own ASCII names.
            if (type == typeof(string)) return new string(_humanText.Contains(name) ? '€' : 'x', 3000);
            if (type.IsEnum) return Enum.GetValues(type).Cast<object>().LastOrDefault() ?? Activator.CreateInstance(type);
            if (type.IsPrimitive) return Activator.CreateInstance(type);

            if (type.IsArray)
            {
                Type element = type.GetElementType()!;
                Array array = Array.CreateInstance(element, depth > 5 ? 0 : 600);
                for (int index = 0; index < array.Length; index++) array.SetValue(Fill(element, depth + 1), index);
                return array;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                IList list = (IList)Activator.CreateInstance(type)!;
                Type element = type.GetGenericArguments()[0];
                for (int index = 0; depth <= 5 && index < 600; index++) list.Add(Fill(element, depth + 1));
                return list;
            }

            if (!type.IsValueType) return null;

            object value = Activator.CreateInstance(type)!;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.IsInitOnly && field.FieldType.IsPrimitive) continue;
                field.SetValue(value, Fill(field.FieldType, depth + 1, field.Name));
            }

            return value;
        }

        /// <summary>Counts what LiteNetLib's writer would produce.</summary>
        private sealed class SizeWriter : INetWriter
        {
            internal int Bytes { get; private set; }

            public void Put(byte value) => Bytes += 1;
            public void Put(sbyte value) => Bytes += 1;
            public void Put(short value) => Bytes += 2;
            public void Put(ushort value) => Bytes += 2;
            public void Put(int value) => Bytes += 4;
            public void Put(uint value) => Bytes += 4;
            public void Put(long value) => Bytes += 8;
            public void Put(ulong value) => Bytes += 8;
            public void Put(float value) => Bytes += 4;
            public void Put(double value) => Bytes += 8;
            public void Put(bool value) => Bytes += 1;
            public void Put(string value) => Bytes += 2 + Encoding.UTF8.GetByteCount(value ?? string.Empty);
            public void Put(byte[] value) => Bytes += 2 + (value?.Length ?? 0);
        }
    }
}
