using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Something one player learned that everybody should know.
    ///
    /// **Knowledge is the one thing coop should not make scarce.** A recipe read from a scrap of paper and
    /// a letter picked up off a corpse are consumed the moment they are used: the item is gone, and with
    /// one world between two players there is no second copy for the other to find. Left alone, whoever
    /// reached the paper first is the only one who can ever craft the thing, and the other player's
    /// journal has a hole in it that nothing in the world will fill.
    ///
    /// **Everyone, both directions.** Unlike every other message here, this is not something one machine
    /// did to another - there is no attacker and no victim. Whoever learns it says so, the host passes it
    /// on, and each machine teaches its own player. That includes the host learning from a client.
    ///
    /// Sent reliably and once. Learning is idempotent on the receiving side - the game refuses a recipe it
    /// already has - so a repeat is harmless, but a lost one is a recipe that never arrives.
    /// </summary>
    public struct KnowledgeMessage : INetMessage
    {
        /// <summary>
        /// The longest id accepted. Real ones are a fixed short length and this is generous, which is the
        /// point: it costs nothing and it stops an id being a way to put a kilobyte of someone else's text
        /// into this machine's log.
        /// </summary>
        public const int MaxIdLength = 64;

        /// <summary>A recipe, named by the guid of its recipe asset.</summary>
        public const byte KindRecipe = 0;

        /// <summary>
        /// A letter or note, named by the readable id of the item that carries it.
        ///
        /// The item rather than the journal topic, because an item id is the one thing both machines can
        /// already resolve - the equipment sync resolves them constantly - while journal topics have no
        /// database to look them up in.
        /// </summary>
        public const byte KindWriting = 1;

        /// <summary>
        /// Who learned it. The host overwrites a client's claim with the id it assigned that peer; nothing
        /// depends on it beyond saying so in the log, but a message that cannot say who it came from is
        /// one nobody can diagnose.
        /// </summary>
        public PlayerId Learner;

        /// <summary>
        /// What kind of thing was learned. Anything this build does not know is ignored rather than
        /// guessed at, so a peer on a later version cannot teach this one something it has no way to
        /// apply.
        /// </summary>
        public byte Kind;

        /// <summary>
        /// What was learned, as an id whose meaning depends on <see cref="Kind"/>. Never null after a
        /// read.
        /// </summary>
        public string Id;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Learner.Value);
            writer.Put(Kind);
            writer.Put(Truncate(Id ?? string.Empty));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Learner = new PlayerId(reader.GetByte());
            Kind = reader.GetByte();

            // Truncated on read as well as on write, because the sender is not the one to trust about how
            // long its own strings are.
            Id = Truncate(reader.GetString() ?? string.Empty);
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxIdLength ? value : value[..MaxIdLength];
        }
    }
}
