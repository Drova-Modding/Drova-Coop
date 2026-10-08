using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// String bounds applied on write and again on read, since the sender is not trusted.
    /// </summary>
    internal static class WireStrings
    {
        /// <summary>The longest guid accepted. Drova's actor guids are 36-character strings.</summary>
        internal const int MaxGuidLength = 36;

        /// <summary>How many guids fit in one chunk, well inside a datagram after relay framing.</summary>
        internal const int MaxGuids = 16;

        /// <summary>Cuts a string to what the wire carries. Null reads as empty.</summary>
        internal static string Cut(string? value, int limit)
        {
            value ??= string.Empty;
            return value.Length <= limit ? value : value[..limit];
        }

        /// <summary>A byte count, then at most that many strings, each cut to the limit. Null writes as empty.</summary>
        internal static void PutStrings(INetWriter writer, string[]? values, int maxCount, int maxLength)
        {
            int count = values == null ? 0 : Math.Min(values.Length, maxCount);

            writer.Put((byte)count);

            for (int index = 0; index < count; index++)
            {
                writer.Put(Cut(values![index], maxLength));
            }
        }

        /// <summary>Reads what <see cref="PutStrings"/> wrote, clamping the count since it sizes the allocation.</summary>
        internal static string[] GetStrings(INetReader reader, int maxCount, int maxLength)
        {
            int count = Math.Min(reader.GetByte(), maxCount);
            string[] values = new string[count];

            for (int index = 0; index < count; index++)
            {
                values[index] = Cut(reader.GetString(), maxLength);
            }

            return values;
        }
    }
}
