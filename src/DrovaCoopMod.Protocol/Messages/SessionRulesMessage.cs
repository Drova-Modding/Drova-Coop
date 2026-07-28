using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to everyone: the session rules changed.
    ///
    /// The same rules ride along with <see cref="HandshakeResultMessage"/>, because a joiner needs them
    /// before it has a body, and this message exists for the rest of the session. Both carry the whole set
    /// rather than a delta - there is one rule today, and a set that is always complete cannot get out of
    /// step with itself if a packet is lost or a client joins between two changes.
    ///
    /// Reliable, and sent only when something actually changed. This is not state to be sampled; a rule
    /// that arrives late is wrong for exactly as long as it takes to arrive, and one that never arrives
    /// leaves two machines disagreeing about who can hurt whom.
    /// </summary>
    public struct SessionRulesMessage : INetMessage
    {
        /// <summary>
        /// Whether players may damage each other. See <see cref="HandshakeResultMessage.FriendlyFire"/>
        /// for why only the host's answer counts.
        /// </summary>
        public bool FriendlyFire;

        /// <summary>
        /// How much health a player gets back when they stand up after being downed, as a percentage of
        /// their maximum. See <see cref="HandshakeResultMessage.RecoveryHealthPercent"/>.
        /// </summary>
        public byte RecoveryHealthPercent;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(FriendlyFire);
            writer.Put(RecoveryHealthPercent);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            FriendlyFire = reader.GetBool();
            RecoveryHealthPercent = reader.GetByte();
        }
    }
}
