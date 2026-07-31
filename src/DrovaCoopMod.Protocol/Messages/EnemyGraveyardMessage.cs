using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Everything the host already knows to be dead, sent to a client that has just joined.
    ///
    /// **Absence of a save record means alive, which is why this has to exist.** The game answers "is this
    /// actor alive" with yes for any creature it has no record of, so a client whose save never recorded a
    /// kill will show that creature standing - and no later message will mention it, because it died
    /// before this client was listening. Without a graveyard, joining a session halfway through means
    /// walking past every camp the host already cleared and finding it full.
    ///
    /// **Guids rather than handles.** A handle only means something after the bind that created it, which
    /// would make this depend on ordering against a different message; a guid means the same thing
    /// whenever it arrives.
    ///
    /// Chunked, because a long session's worth of dead creatures does not fit in a datagram. The last
    /// chunk says so, so the receiver knows when it has the whole picture.
    /// </summary>
    public struct EnemyGraveyardMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = 36;

        /// <summary>
        /// How many guids fit in one chunk. Sized so a full chunk stays well inside a datagram even after
        /// the relay's framing and encryption.
        /// </summary>
        public const int MaxGuids = 16;

        /// <summary>The dead, by guid. Never null after a read.</summary>
        public string[] Guids;

        /// <summary>
        /// Non-zero when more chunks follow. A receiver can act on each chunk as it arrives; this only
        /// tells it when to stop expecting more.
        /// </summary>
        public byte More;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            string[] guids = Guids ?? [];
            int count = guids.Length <= MaxGuids ? guids.Length : MaxGuids;

            writer.Put((byte)count);

            for (int index = 0; index < count; index++)
            {
                writer.Put(Truncate(guids[index] ?? string.Empty));
            }

            writer.Put(More);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            // Bounded on read as well as on write. The count decides how much this machine allocates, so
            // it is not a number the sender gets to choose freely.
            int count = reader.GetByte();
            if (count > MaxGuids) count = MaxGuids;

            Guids = new string[count];

            for (int index = 0; index < count; index++)
            {
                Guids[index] = Truncate(reader.GetString() ?? string.Empty);
            }

            More = reader.GetByte();
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxGuidLength ? value : value[..MaxGuidLength];
        }
    }
}
