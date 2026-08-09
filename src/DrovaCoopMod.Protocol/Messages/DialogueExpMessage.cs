using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Experience a dialogue granted one player, paid to everybody.
    ///
    /// A dialogue's reward node pays the player standing in the conversation and nobody else. Kill
    /// experience already crosses with its own dedup; this carries the dialogue kind, which has no
    /// natural identity to dedup by - so the host relays it with the sender excluded, the same way
    /// cutscenes travel, because paying twice is not a no-op.
    /// </summary>
    public struct DialogueExpMessage : INetMessage
    {
        /// <summary>The largest grant accepted. Vanilla dialogue rewards are two to three digits.</summary>
        public const int MaxAmount = 10000;

        /// <summary>The longest identity accepted.</summary>
        public const int MaxIdentityLength = 96;

        /// <summary>How much was granted.</summary>
        public int Amount;

        /// <summary>
        /// Which reward node granted it, as dialogue tree plus node id. What lets both machines pay
        /// each reward exactly once however many players sit through the same conversation.
        /// </summary>
        public string Identity;

        /// <summary>Whether the figure is one a real reward could be.</summary>
        public readonly bool IsPlausible()
        {
            return Amount > 0 && Amount <= MaxAmount;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Amount);
            writer.Put(Truncate(Identity ?? string.Empty));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Amount = reader.GetInt();
            Identity = Truncate(reader.GetString() ?? string.Empty);
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxIdentityLength ? value : value[..MaxIdentityLength];
        }
    }
}
