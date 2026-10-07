using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The talents one player has learned, by guid. Their copies need them because combo steps and timing windows are read off the body.
    /// </summary>
    public struct PlayerTalentsMessage : INetMessage
    {
        /// <summary>
        /// The most talents one relayed packet fits. Longer lists use <see cref="Split"/>.
        /// </summary>
        public const int MaxTalents = 17;

        /// <summary>The longest guid kept, cut on write and on read.</summary>
        public const int MaxGuidLength = 64;

        /// <summary>Whose talents these are. Stamped by the session.</summary>
        public PlayerId PlayerId;

        /// <summary>The learned talents' guids.</summary>
        public string[] Guids;

        /// <summary>More parts follow. The receiver waits for the last part before replacing its list.</summary>
        public bool More;

        /// <summary>
        /// Splits into relayable parts. Always at least one, since an empty list is news too.
        /// </summary>
        public readonly PlayerTalentsMessage[] Split()
        {
            string[] all = Guids ?? [];
            PlayerTalentsMessage[] parts = new PlayerTalentsMessage[Math.Max(1, (all.Length + MaxTalents - 1) / MaxTalents)];

            for (int index = 0; index < parts.Length; index++)
            {
                parts[index] = this;
                parts[index].Guids = [.. all.Skip(index * MaxTalents).Take(MaxTalents)];
                parts[index].More = index < parts.Length - 1;
            }

            return parts;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            string[] guids = Guids ?? [];
            byte count = (byte)Math.Min(guids.Length, MaxTalents);

            writer.Put(PlayerId.Value);
            writer.Put(More);
            writer.Put(count);
            for (int index = 0; index < count; index++)
            {
                writer.Put(WireStrings.Cut(guids[index], MaxGuidLength));
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            PlayerId = new PlayerId(reader.GetByte());
            More = reader.GetBool();
            int count = Math.Min((int)reader.GetByte(), MaxTalents);
            Guids = new string[count];
            for (int index = 0; index < count; index++)
            {
                Guids[index] = WireStrings.Cut(reader.GetString(), MaxGuidLength);
            }
        }
    }
}
