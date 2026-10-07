using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Journal entries one player earned, as resource paths, since quest graph progress never crosses.
    /// </summary>
    public struct JournalEntryMessage : INetMessage
    {
        /// <summary>How many entry paths one message carries.</summary>
        public const int MaxPaths = 7;

        /// <summary>The longest resource path accepted.</summary>
        public const int MaxPathLength = 160;

        /// <summary>The entries, as the resource paths the game's save stores them under.</summary>
        public string[] Paths;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            WireStrings.PutStrings(writer, Paths, MaxPaths, MaxPathLength);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Paths = WireStrings.GetStrings(reader, MaxPaths, MaxPathLength);
        }
    }

    /// <summary>A journal topic moving category, which marks a quest completed or failed.</summary>
    public struct JournalTopicMessage : INetMessage
    {
        /// <summary>The topic, as the resource path the game's save stores.</summary>
        public string TopicPath;

        /// <summary>The category it now sits in, same identity.</summary>
        public string CategoryPath;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(TopicPath, JournalEntryMessage.MaxPathLength));
            writer.Put(WireStrings.Cut(CategoryPath, JournalEntryMessage.MaxPathLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            TopicPath = WireStrings.Cut(reader.GetString(), JournalEntryMessage.MaxPathLength);
            CategoryPath = WireStrings.Cut(reader.GetString(), JournalEntryMessage.MaxPathLength);
        }
    }
}
