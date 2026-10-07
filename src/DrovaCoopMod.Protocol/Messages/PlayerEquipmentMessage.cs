using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a player wears and carries, as readable item ids. Sent reliably on change. A body needs the
    /// same weapon for its attacks to bind to the right action.
    /// </summary>
    public struct PlayerEquipmentMessage : INetMessage
    {
        /// <summary>
        /// The most items one message carries at the longest ids. Larger outfits cross in parts (<see cref="Split"/>).
        /// </summary>
        public const int MaxItems = 17;

        /// <summary>The longest item id accepted.</summary>
        public const int MaxItemIdLength = 64;

        /// <summary>Whose equipment this is. The host overwrites a client's claim with that peer's assigned id.</summary>
        public PlayerId PlayerId;

        /// <summary>Wire value for a slot shared by every set. The game's -1 does not fit a byte.</summary>
        public const byte SharedSet = 255;

        /// <summary>Everything equipped, across both weapon sets. Never null after a read.</summary>
        public string[] ItemIds;

        /// <summary>
        /// The weapon set of each item in <see cref="ItemIds"/>, or <see cref="SharedSet"/>. Both sets travel so
        /// shared-slot ammo arrives with its bow. Never null after a read.
        /// </summary>
        public byte[] ItemSets;

        /// <summary>Which of the sender's weapon sets is in hand.</summary>
        public byte ActiveSet;

        /// <summary>
        /// The sender's build, as <c>ActorBodyType.BodyType</c>. A component rather than equipment, so a body
        /// cloned from the player prefab keeps the prefab's build otherwise.
        /// </summary>
        public byte BodyType;

        /// <summary>The sender's voice, as <c>VoiceID</c>.</summary>
        public byte VoiceId;

        /// <summary>More parts of this outfit follow. The receiver waits for the last part.</summary>
        public bool More;

        /// <summary>This message split into relay-sized parts. Always at least one, since wearing nothing is news too.</summary>
        public readonly PlayerEquipmentMessage[] Split()
        {
            string[] ids = ItemIds ?? [];
            byte[] sets = ItemSets ?? [];
            PlayerEquipmentMessage[] parts = new PlayerEquipmentMessage[Math.Max(1, (ids.Length + MaxItems - 1) / MaxItems)];

            for (int index = 0; index < parts.Length; index++)
            {
                parts[index] = this;
                parts[index].ItemIds = [.. ids.Skip(index * MaxItems).Take(MaxItems)];
                parts[index].ItemSets = [.. sets.Skip(index * MaxItems).Take(MaxItems)];
                parts[index].More = index < parts.Length - 1;
            }

            return parts;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            string[] ids = ItemIds ?? [];
            byte[] sets = ItemSets ?? [];
            byte count = (byte)Math.Min(ids.Length, MaxItems);

            writer.Put(PlayerId.Value);
            writer.Put(BodyType);
            writer.Put(VoiceId);
            writer.Put(ActiveSet);
            writer.Put(More);
            writer.Put(count);

            for (int index = 0; index < count; index++)
            {
                // The set travels with its item, so the two lists cannot disagree in length.
                writer.Put(index < sets.Length ? sets[index] : SharedSet);
                writer.Put(WireStrings.Cut(ids[index], MaxItemIdLength));
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            PlayerId = new PlayerId(reader.GetByte());
            BodyType = reader.GetByte();
            VoiceId = reader.GetByte();
            ActiveSet = reader.GetByte();
            More = reader.GetBool();

            int count = Math.Min((int)reader.GetByte(), MaxItems);
            ItemIds = new string[count];
            ItemSets = new byte[count];

            for (int index = 0; index < count; index++)
            {
                ItemSets[index] = reader.GetByte();

                // Truncate on read too, the sender is not trusted.
                ItemIds[index] = WireStrings.Cut(reader.GetString(), MaxItemIdLength);
            }
        }
    }
}
