using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A recipe or writing one player learned, relayed so every player learns it. Recipe items are
    /// consumed on use, so there is no second copy to find.
    /// </summary>
    public struct KnowledgeMessage : INetMessage
    {
        /// <summary>The longest id accepted.</summary>
        public const int MaxIdLength = 64;

        /// <summary>A recipe, named by the guid of its recipe asset.</summary>
        public const byte KindRecipe = 0;

        /// <summary>
        /// A letter or note, named by the readable id of its item, since journal topics have no lookup database.
        /// </summary>
        public const byte KindWriting = 1;

        /// <summary>Who learned it. The host overwrites a client's claim with the id it assigned that peer.</summary>
        public PlayerId Learner;

        /// <summary>What kind of thing was learned. Unknown kinds are ignored.</summary>
        public byte Kind;

        /// <summary>
        /// What was learned, as an id whose meaning depends on <see cref="Kind"/>. Never null after a read.
        /// </summary>
        public string Id;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Learner.Value);
            writer.Put(Kind);
            writer.Put(WireStrings.Cut(Id, MaxIdLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Learner = new PlayerId(reader.GetByte());
            Kind = reader.GetByte();

            Id = WireStrings.Cut(reader.GetString(), MaxIdLength);
        }
    }
}
