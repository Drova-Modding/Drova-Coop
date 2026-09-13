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

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(PlayerId.Value);
            writer.Put(MaxHealth);
            writer.Put(MaxStamina);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            PlayerId = new PlayerId(reader.GetByte());
            MaxHealth = reader.GetUShort();
            MaxStamina = reader.GetUShort();
        }
    }
}
