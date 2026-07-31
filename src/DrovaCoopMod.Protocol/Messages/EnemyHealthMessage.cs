using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a creature's health is, as decided by the host.
    ///
    /// **Absolute, not a delta.** A delta is smaller and cannot survive a loss: one dropped packet and
    /// every later number is wrong by the amount that went missing, with nothing to correct it. An
    /// absolute value makes every message a repair of all the ones before it.
    ///
    /// **Reliable and on change, rather than in the position stream.** Health is not like a position: it
    /// changes rarely, it matters exactly, and a rounded copy of it arriving fifteen times a second would
    /// fight the precise value that arrived on the frame the blow landed - a bar that jitters against
    /// itself. So it travels alone, coalesced to a sensible rate, except for the change to zero, which is
    /// sent the instant it happens because it is the one that cannot be superseded.
    /// </summary>
    public struct EnemyHealthMessage : INetMessage
    {
        /// <summary>Which creature.</summary>
        public EnemyNetId NetId;

        /// <summary>Its health now.</summary>
        public ushort Health;

        /// <summary>
        /// Its maximum, carried alongside so the receiver can tell whether its own copy has finished
        /// initializing. A body still running its equipment setup has a maximum that is about to change
        /// and a current health that will be reset to full with it, so a value written before then is
        /// silently stamped over.
        /// </summary>
        public ushort MaxHealth;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(NetId.Value);
            writer.Put(Health);
            writer.Put(MaxHealth);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());
            Health = reader.GetUShort();
            MaxHealth = reader.GetUShort();
        }
    }
}
