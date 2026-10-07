using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A player's health and stamina maxima, sent reliably on change and when the roster grows.
    /// A body that has not heard it yet reads both as zero and applies neither bar.
    /// </summary>
    public struct PlayerVitalsMessage : INetMessage
    {
        /// <summary>Whose. The host overwrites it with the id it handed the sending connection.</summary>
        public PlayerId PlayerId;

        /// <summary>Maximum health, capped to the wire.</summary>
        public ushort MaxHealth;

        /// <summary>Maximum stamina, capped to the wire.</summary>
        public ushort MaxStamina;

        /// <summary>Once-per-character facts, one per bit.</summary>
        public byte Flags;

        /// <summary>
        /// The player has the perfect-block talent. Puppets carry no talents, so without it a remote
        /// perfect block shows as an ordinary one.
        /// </summary>
        public const byte FlagPerfectBlock = 1;

        /// <summary>Whether this player can perfect-block at all.</summary>
        public readonly bool HasPerfectBlock => (Flags & FlagPerfectBlock) != 0;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(PlayerId.Value);
            writer.Put(MaxHealth);
            writer.Put(MaxStamina);
            writer.Put(Flags);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            PlayerId = new PlayerId(reader.GetByte());
            MaxHealth = reader.GetUShort();
            MaxStamina = reader.GetUShort();
            Flags = reader.GetByte();
        }
    }
}
