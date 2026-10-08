using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Experience a dialogue granted one player, paid to everybody. The host relays it with the sender excluded.
    /// </summary>
    public struct DialogueExpMessage : INetMessage
    {
        /// <summary>The largest grant accepted. Vanilla dialogue rewards are two to three digits.</summary>
        public const int MaxAmount = 10000;

        /// <summary>The longest identity accepted.</summary>
        private const int MaxIdentityLength = 96;

        /// <summary>How much was granted.</summary>
        public int Amount;

        /// <summary>Dialogue tree plus reward node id, so each reward is paid once per machine.</summary>
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
            writer.Put(WireStrings.Cut(Identity, MaxIdentityLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Amount = reader.GetInt();
            Identity = WireStrings.Cut(reader.GetString(), MaxIdentityLength);
        }
    }
}
