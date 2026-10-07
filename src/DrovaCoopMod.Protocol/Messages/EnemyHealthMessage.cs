using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A creature's absolute health as decided by the host, so each message repairs any lost before it.
    /// Sent reliably on change, coalesced except for the drop to zero.
    /// </summary>
    public struct EnemyHealthMessage : INetMessage
    {
        /// <summary>Which creature.</summary>
        public EnemyNetId NetId;

        /// <summary>Its health now.</summary>
        public ushort Health;

        /// <summary>
        /// Its maximum, so the receiver can tell whether its copy finished equipment setup,
        /// which resets health to full and would stamp over an earlier write.
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
