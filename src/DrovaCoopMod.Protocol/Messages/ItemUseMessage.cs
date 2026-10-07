using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A player started or stopped using an item (ability or consumable) via <c>Item.StartUseItem</c>, which no action bind covers.
    /// The far side replays the use on its copy. The resulting spell deals no damage there (see <c>ItemUseWatch</c>).
    /// </summary>
    public struct ItemUseMessage : INetMessage
    {
        /// <summary>The longest item id accepted.</summary>
        public const int MaxItemUseIdLength = 64;

        /// <summary>Who used it. The host stamps a client's claim with its assigned id.</summary>
        public PlayerId User;

        /// <summary>
        /// The item's readable id. Never null after a read.
        /// </summary>
        public string ItemId;

        /// <summary>
        /// Which use this is, wrapping at a byte. Tells a fresh use from a duplicate or a late start.
        /// </summary>
        public byte Sequence;

        /// <summary>
        /// Zero while held, one when let go. A release is the same use, not a new sequence.
        /// </summary>
        public byte Released;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(User.Value);
            writer.Put(Sequence);
            writer.Put(Released);
            writer.Put(WireStrings.Cut(ItemId, MaxItemUseIdLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            User = new PlayerId(reader.GetByte());
            Sequence = reader.GetByte();
            Released = reader.GetByte();

            ItemId = WireStrings.Cut(reader.GetString(), MaxItemUseIdLength);
        }
    }
}
