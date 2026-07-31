using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host claiming one creature: from now on its health is the host's to decide, and this is the
    /// handle the rest of the conversation will use for it.
    ///
    /// Claiming is not a permission, it is a promise the host can keep. The host only binds a creature
    /// it has a live body for, so the moment it cannot resolve one it releases - which is what makes every
    /// failure mode here degrade to "both machines simulate it independently", the state the mod shipped
    /// with, rather than to a creature that is invincible on one screen and dead on the other.
    ///
    /// Carries the health at the moment of binding so the client does not have to wait for the first
    /// change to agree, and the maximum so it can tell whether its own copy has finished initializing -
    /// equipment setup writes the maximum and refills to full some frames after a body exists, which would
    /// otherwise stamp over the first authoritative value.
    /// </summary>
    public struct EnemyBindMessage : INetMessage
    {
        /// <summary>
        /// The longest guid accepted, which is the length every guid in the game actually is.
        /// </summary>
        public const int MaxGuidLength = 36;

        /// <summary>
        /// Set when the host also drives this creature's body - its position, facing and swings - rather
        /// than only its health.
        /// </summary>
        public const byte FlagHostDrives = 1 << 0;

        /// <summary>
        /// Set when the creature is already dead on the host at the moment of binding, so a client that
        /// streams it in later shows a corpse instead of killing it again in front of the player.
        /// </summary>
        public const byte FlagDeadAtBind = 1 << 1;

        /// <summary>The handle this creature will be known by for the rest of the session.</summary>
        public EnemyNetId NetId;

        /// <summary>Which creature, by the guid its scene was authored with. Never null after a read.</summary>
        public string ActorGuid;

        /// <summary>Its health on the host right now.</summary>
        public ushort Health;

        /// <summary>Its maximum health on the host right now.</summary>
        public ushort MaxHealth;

        /// <summary>See <see cref="FlagHostDrives"/> and <see cref="FlagDeadAtBind"/>.</summary>
        public byte Flags;

        /// <summary>Whether the host is driving this body as well as owning its health.</summary>
        public readonly bool HostDrives => (Flags & FlagHostDrives) != 0;

        /// <summary>Whether this creature was already dead when the host claimed it.</summary>
        public readonly bool IsDeadAtBind => (Flags & FlagDeadAtBind) != 0;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(NetId.Value);
            writer.Put(Truncate(ActorGuid ?? string.Empty));
            writer.Put(Health);
            writer.Put(MaxHealth);
            writer.Put(Flags);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());

            // Truncated on read as well as on write, because the sender is not the one to trust about how
            // long its own strings are.
            ActorGuid = Truncate(reader.GetString() ?? string.Empty);
            Health = reader.GetUShort();
            MaxHealth = reader.GetUShort();
            Flags = reader.GetByte();
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxGuidLength ? value : value[..MaxGuidLength];
        }
    }
}
