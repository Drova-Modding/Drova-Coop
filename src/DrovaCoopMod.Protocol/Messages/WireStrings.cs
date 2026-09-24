using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The string bounds every message applies on write and again on read, because the sender is not
    /// the one to trust about how long its own strings are or how many it sent.
    /// </summary>
    internal static class WireStrings
    {
        /// <summary>The longest guid accepted. Drova's actor guids are 36-character strings.</summary>
        internal const int MaxGuidLength = 36;

        /// <summary>
        /// How many guids fit in one chunk. Sized so a full chunk stays well inside a datagram even after
        /// the relay's framing and encryption.
        /// </summary>
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

        /// <summary>
        /// Reads what <see cref="PutStrings"/> wrote. The count is clamped because it decides how much
        /// this machine allocates. Never returns null.
        /// </summary>
        internal static string[] GetStrings(INetReader reader, int maxCount, int maxLength)
        {
            int count = Math.Min((int)reader.GetByte(), maxCount);
            string[] values = new string[count];

            for (int index = 0; index < count; index++)
            {
                values[index] = Cut(reader.GetString(), maxLength);
            }

            return values;
        }
    }
}
