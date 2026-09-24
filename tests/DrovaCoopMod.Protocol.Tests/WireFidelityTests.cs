using System.Reflection;
using Drova_Modding_API.Systems.Networking;
using Xunit;

namespace DrovaCoopMod.Messages.Tests
{
    /// <summary>
    /// Every message, every field, mechanically: each public field is given a value no other field of
    /// the message has, the message is written and read back twice, and the two read-backs are compared.
    ///
    /// The hand-written tests beside this one check what each message means. This one checks only what
    /// they cannot be relied on to: that no two fields of the same type are written in one order and
    /// read in another. A hand-written test filling two floats with the same number passes with them
    /// swapped; this one cannot, because no two fields ever share a value.
    ///
    /// Two read-backs rather than sent against received, because reading is allowed to normalize: a
    /// damage type outside the three the wire knows becomes physical, a count larger than its array is
    /// capped, a fixed-length array is always its full length. A read like that is idempotent, so the
    /// second pass must reproduce the first exactly. A swap is still caught: the second pass swaps back.
    /// </summary>
    public class WireFidelityTests
    {
        /// <summary>
        /// Every struct in the protocol assembly that goes on the wire.
        /// </summary>
        public static IEnumerable<object[]> Messages()
        {
            foreach (Type type in typeof(CoopProtocol).Assembly.GetTypes())
            {
                if (!type.IsValueType || type.IsNested) continue;
                if (!typeof(INetMessage).IsAssignableFrom(type)) continue;

                yield return new object[] { type };
            }
        }

        [Theory]
        [MemberData(nameof(Messages))]
        public void EveryField_ComesBackWhereItWent(Type messageType)
        {
            int seed = 1;
            object sent = Fill(messageType, ref seed);

            object once = RoundTrip(sent);
            object twice = RoundTrip(once);

            List<string> differences = new();
            Compare(messageType.Name, messageType, once, twice, differences);

            Assert.True(differences.Count == 0, messageType.Name + ":\n  " + string.Join("\n  ", differences));
        }

        /// <summary>
        /// The filler must actually reach every field, or a message whose fields it skipped would pass
        /// on defaults, and a field that is never written reads back as its default whatever was sent.
        /// So every field has to come back non-default for some fill. One fill is not enough: booleans
        /// alternate, and a byte outside the few a field allows is read back as a default fallback.
        /// </summary>
        [Theory]
        [MemberData(nameof(Messages))]
        public void EveryField_IsCarried(Type messageType)
        {
            HashSet<string>? uncarried = null;

            // The filler's bytes cycle through a hundred values, so this many fills reach every one.
            for (int start = 1; start <= 100 && uncarried is not { Count: 0 }; start++)
            {
                int seed = start;
                object sent = Fill(messageType, ref seed);
                object once = RoundTrip(sent);

                List<string> defaults = new();
                DefaultLeaves(messageType.Name, messageType, once, sent, defaults);

                if (uncarried == null) uncarried = new HashSet<string>(defaults);
                else uncarried.IntersectWith(defaults);
            }

            Assert.True(uncarried!.Count == 0, messageType.Name + " read back defaults:\n  " + string.Join("\n  ", uncarried));
        }

        [Fact]
        public void TheListOfMessages_IsNotEmpty()
        {
            Assert.True(Messages().Count() > 40);
        }

        private static object RoundTrip(object message)
        {
            using MemoryStream stream = new();
            ((INetMessage)message).Write(new BufferWriter(stream));

            stream.Position = 0;
            object received = Activator.CreateInstance(message.GetType())!;

            // Boxed on purpose: Read mutates the struct, and a call through the interface on the box
            // mutates the box, which is what is compared afterwards.
            ((INetMessage)received).Read(new BufferReader(stream));

            return received;
        }

        /// <summary>
        /// A value of the type no earlier call in this message produced. Kept small so that no message's
        /// own bounds clip it: names are truncated, counts are capped, and a clipped value would read as
        /// a field out of order when it is not.
        /// </summary>
        private static object Fill(Type type, ref int seed)
        {
            int n = seed++;

            if (type == typeof(byte)) return (byte)(1 + n % 100);
            if (type == typeof(sbyte)) return (sbyte)(1 + n % 100);
            if (type == typeof(short)) return (short)(1 + n);
            if (type == typeof(ushort)) return (ushort)(1 + n);
            if (type == typeof(int)) return 1 + n;
            if (type == typeof(uint)) return (uint)(1 + n);
            if (type == typeof(long)) return 1L + n;
            if (type == typeof(ulong)) return (ulong)(1 + n);
            if (type == typeof(float)) return 1f + n + 0.25f;
            if (type == typeof(double)) return 1d + n + 0.5d;
            if (type == typeof(bool)) return n % 2 == 1;
            if (type == typeof(string)) return "f" + n;
            if (type.IsEnum) return Enum.ToObject(type, 1 + n % 100);

            if (type.IsArray)
            {
                Type element = type.GetElementType()!;
                Array array = Array.CreateInstance(element, 2);

                for (int index = 0; index < array.Length; index++)
                {
                    array.SetValue(Fill(element, ref seed), index);
                }

                return array;
            }

            FieldInfo[] fields = PublicFields(type);
            if (fields.Length > 0)
            {
                object instance = Activator.CreateInstance(type)!;

                foreach (FieldInfo field in fields)
                {
                    field.SetValue(instance, Fill(field.FieldType, ref seed));
                }

                return instance;
            }

            // A wrapper such as PlayerId, or a positional record such as RosterEntry: no public fields,
            // so the constructor with the most parameters is the only way in.
            ConstructorInfo? constructor = type.GetConstructors()
                .Where(candidate => candidate.GetParameters().Length > 0)
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .FirstOrDefault();

            if (constructor == null)
            {
                throw new InvalidOperationException("No way to fill a " + type.Name + "; teach Fill about it.");
            }

            ParameterInfo[] parameters = constructor.GetParameters();
            object[] arguments = new object[parameters.Length];

            for (int index = 0; index < parameters.Length; index++)
            {
                arguments[index] = Fill(parameters[index].ParameterType, ref seed);
            }

            return constructor.Invoke(arguments);
        }

        private static void Compare(string path, Type type, object? left, object? right, List<string> differences)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string))
            {
                if (!Equals(left, right)) differences.Add($"{path}: first {left ?? "null"}, second {right ?? "null"}");
                return;
            }

            if (type.IsArray)
            {
                Array? leftArray = left as Array;
                Array? rightArray = right as Array;

                if (leftArray == null || rightArray == null || leftArray.Length != rightArray.Length)
                {
                    differences.Add($"{path}: first {Describe(leftArray)}, second {Describe(rightArray)}");
                    return;
                }

                Type element = type.GetElementType()!;
                for (int index = 0; index < leftArray.Length; index++)
                {
                    Compare($"{path}[{index}]", element, leftArray.GetValue(index), rightArray.GetValue(index), differences);
                }

                return;
            }

            FieldInfo[] fields = PublicFields(type);
            if (fields.Length > 0)
            {
                foreach (FieldInfo field in fields)
                {
                    Compare(path + "." + field.Name, field.FieldType, field.GetValue(left), field.GetValue(right), differences);
                }

                return;
            }

            // Wrappers and records expose what they hold as properties.
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                .ToArray();

            if (properties.Length > 0)
            {
                foreach (PropertyInfo property in properties)
                {
                    Compare(path + "." + property.Name, property.PropertyType, property.GetValue(left), property.GetValue(right), differences);
                }

                return;
            }

            if (!Equals(left, right)) differences.Add($"{path}: first {left}, second {right}");
        }

        /// <summary>
        /// Every leaf of a value that holds its type's default, where null, an empty string and an empty
        /// array count as default. Walks the same shape <see cref="Compare"/> does, but only as far into
        /// an array as was sent, since a fixed-length array is padded with defaults on read.
        /// </summary>
        private static void DefaultLeaves(string path, Type type, object? value, object? sent, List<string> found)
        {
            if (type.IsPrimitive || type.IsEnum)
            {
                if (Equals(value, Activator.CreateInstance(type))) found.Add(path);
                return;
            }

            if (type == typeof(string))
            {
                if (string.IsNullOrEmpty((string?)value)) found.Add(path);
                return;
            }

            if (type.IsArray)
            {
                Array? array = value as Array;
                if (array == null || array.Length == 0)
                {
                    found.Add(path);
                    return;
                }

                Array? sentArray = sent as Array;
                int length = Math.Min(array.Length, sentArray?.Length ?? 0);

                Type element = type.GetElementType()!;
                for (int index = 0; index < length; index++)
                {
                    DefaultLeaves($"{path}[{index}]", element, array.GetValue(index), sentArray!.GetValue(index), found);
                }

                return;
            }

            FieldInfo[] fields = PublicFields(type);
            if (fields.Length > 0)
            {
                foreach (FieldInfo field in fields)
                {
                    DefaultLeaves(path + "." + field.Name, field.FieldType, field.GetValue(value), field.GetValue(sent), found);
                }

                return;
            }

            // Wrappers and records: what the constructor filled is the non-computed state.
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                if (property.PropertyType == typeof(bool)) continue;

                DefaultLeaves(path + "." + property.Name, property.PropertyType, property.GetValue(value), property.GetValue(sent), found);
            }
        }

        private static FieldInfo[] PublicFields(Type type)
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(field => !field.IsInitOnly)
                .ToArray();
        }

        private static string Describe(Array? array)
        {
            if (array == null) return "null";

            return "[" + string.Join(", ", array.Cast<object>().Select(item => item?.ToString() ?? "null")) + "]";
        }
    }
}
