using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host claiming one creature's health and naming its handle. The host releases it as soon as it has no live body.
    /// </summary>
    public struct EnemyBindMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>Set when the host also drives this creature's body, not only its health.</summary>
        public const byte FlagHostDrives = 1 << 0;

        /// <summary>Set when the creature is already dead on the host, so a late client shows a corpse.</summary>
        public const byte FlagDeadAtBind = 1 << 1;

        /// <summary>The handle this creature will be known by for the rest of the session.</summary>
        public EnemyNetId NetId;

        /// <summary>Which creature, by the guid its scene was authored with. Never null after a read.</summary>
        public string ActorGuid;

        /// <summary>Its health on the host right now.</summary>
        public ushort Health;

        /// <summary>Its maximum health on the host, so the client can tell whether its own copy finished initializing.</summary>
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
            writer.Put(WireStrings.Cut(ActorGuid, MaxGuidLength));
            writer.Put(Health);
            writer.Put(MaxHealth);
            writer.Put(Flags);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());
            ActorGuid = WireStrings.Cut(reader.GetString(), MaxGuidLength);
            Health = reader.GetUShort();
            MaxHealth = reader.GetUShort();
            Flags = reader.GetByte();
        }
    }
}
