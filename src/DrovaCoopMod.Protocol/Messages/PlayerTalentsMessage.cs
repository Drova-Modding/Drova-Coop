using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The talents one player has learned, by guid. A copy of that player has to carry them: the game asks the
    /// body itself whether a combo step, a charged attack's timing or a guard's perfect window is unlocked, and a
    /// copy that only ever had the talents it was cloned with swung differently from its owner. Sent reliably on
    /// change and whenever the roster grows, like the equipment.
    /// </summary>
    public struct PlayerTalentsMessage : INetMessage
    {
        /// <summary>
        /// The most talents one message carries: what fits a relayed packet at the longest guids. A longer
        /// list crosses in several, see <see cref="Split"/>.
        /// </summary>
        public const int MaxTalents = 17;

        /// <summary>The longest guid kept, cut on write and on read.</summary>
        public const int MaxGuidLength = 64;

        /// <summary>Whose talents these are. Stamped by the session, never trusted from the sender.</summary>
        public PlayerId PlayerId;

        /// <summary>The learned talents' guids.</summary>
        public string[] Guids;

        /// <summary>
        /// More parts of this list follow. The receiver replaces what it knew with what arrives, so it
        /// waits for the last part rather than keep only that one.
        /// </summary>
        public bool More;

        /// <summary>
        /// This message in the parts that fit what a relay forwards, every one but the last saying
        /// <see cref="More"/> follow. Always at least one, because having learned nothing is news too.
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
