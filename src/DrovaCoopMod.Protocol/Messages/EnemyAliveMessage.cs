using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Creatures a client asked about that the host's world still has standing.
    ///
    /// The other half of the <see cref="WorldStateRequestMessage"/> answer. The graveyard names the
    /// dead, and for years of the mod's life "alive" was only ever implied by absence - which the client
    /// could not act on, because the thing that turns a local corpse back into a creature needed a bind,
    /// and a bind needs the host to have a body. A player standing in a camp two thousand units from the
    /// host was surrounded by corpses the host knew to be alive and had no way to say so.
    ///
    /// This says so. It is answered from the host's save, which is readable at any distance, so the
    /// client can give the corpse a new body without waiting for the host to walk over and claim it.
    ///
    /// Guids rather than handles, for the graveyard's reason: a handle only exists after a bind, and
    /// this message exists precisely for creatures that have never had one.
    /// </summary>
    public struct EnemyAliveMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = 36;

        /// <summary>
        /// How many guids fit in one chunk. Sized so a full chunk stays well inside a datagram even after
        /// the relay's framing and encryption.
        /// </summary>
        public const int MaxGuids = 16;

        /// <summary>The living, by guid. Never null after a read.</summary>
        public string[] Guids;

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
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxGuidLength ? value : value[..MaxGuidLength];
        }
    }
}
