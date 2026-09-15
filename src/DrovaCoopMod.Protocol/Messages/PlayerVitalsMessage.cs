using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a player's health and stamina bars are out of. Sent when it changes and whenever the roster
    /// grows, so a joiner learns it too, rather than thirty times a second beside the position it has
    /// nothing to do with. Reliable, so a change cannot be lost the way an unreliable tick can.
    ///
    /// A body that has never heard this for its player reads both maxima as zero, and applies neither
    /// bar until it does, which is the same rule the tick carried when the maxima rode with it.
    /// </summary>
    public struct PlayerVitalsMessage : INetMessage
    {
        /// <summary>Whose. The host overwrites it with the id it handed the sending connection.</summary>
        public PlayerId PlayerId;

        /// <summary>Maximum health, capped to the wire.</summary>
        public ushort MaxHealth;

        /// <summary>Maximum stamina, capped to the wire.</summary>
        public ushort MaxStamina;

        /// <summary>
        /// Facts about this player that change once in a character's life, packed one to a bit. Only
        /// <see cref="FlagPerfectBlock"/> so far.
        /// </summary>
        public byte Flags;

        /// <summary>
        /// Set when this player has learned the talent that opens the perfect-block window.
        ///
        /// It has to cross because the window is gated on the talent, and a puppet carries none: without
        /// it a remote body can never enter the window at all, so every perfect block its owner lands
        /// reads as an ordinary one on every other machine - wrong spark, wrong sound, and the wrong one
        /// of the two status effects a block lays on whoever swung.
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
