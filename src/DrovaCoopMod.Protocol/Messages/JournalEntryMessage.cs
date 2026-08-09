using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Journal entries one player earned, so every journal tells the same story.
    ///
    /// The journal is written by quest graphs whose progress lives in each machine's scenes and never
    /// crosses, so no amount of variable syncing fills a joining player's journal. The entries
    /// themselves are just authored assets: what crosses is their resource paths - the same identity
    /// the game's own save stores - and the far side triggers them through the game's own path, which
    /// deduplicates, pays the entry's rewards once, and autosaves.
    /// </summary>
    public struct JournalEntryMessage : INetMessage
    {
        /// <summary>How many entry paths one message carries.</summary>
        public const int MaxPaths = 8;

        /// <summary>The longest resource path accepted.</summary>
        public const int MaxPathLength = 160;

        /// <summary>The entries, as the resource paths the game's save stores them under.</summary>
        public string[] Paths;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            int count = Paths == null ? 0 : System.Math.Min(Paths.Length, MaxPaths);

            writer.Put((byte)count);

            for (int index = 0; index < count; index++)
            {
                writer.Put(Truncate(Paths![index] ?? string.Empty));
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            int count = System.Math.Min((int)reader.GetByte(), MaxPaths);

            Paths = new string[count];

            for (int index = 0; index < count; index++)
            {
                Paths[index] = Truncate(reader.GetString() ?? string.Empty);
            }
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxPathLength ? value : value[..MaxPathLength];
        }
    }

    /// <summary>
    /// A journal topic moving between categories, which is how a quest reads as completed or failed
    /// in the journal. The entries carry the text; this carries the checkmark.
    /// </summary>
    public struct JournalTopicMessage : INetMessage
    {
        /// <summary>The topic, as the resource path the game's save stores.</summary>
        public string TopicPath;

        /// <summary>The category it now sits in, same identity.</summary>
        public string CategoryPath;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Truncate(TopicPath ?? string.Empty));
            writer.Put(Truncate(CategoryPath ?? string.Empty));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            TopicPath = Truncate(reader.GetString() ?? string.Empty);
            CategoryPath = Truncate(reader.GetString() ?? string.Empty);
        }

        private static string Truncate(string value)
        {
            return value.Length <= JournalEntryMessage.MaxPathLength
                ? value
                : value[..JournalEntryMessage.MaxPathLength];
        }
    }
}
