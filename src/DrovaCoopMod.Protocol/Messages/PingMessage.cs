using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Measures the round trip to another player, and nothing else.
    ///
    /// The transport reports a ping too, but through a relay that is the time to the relay - which sits
    /// somewhere between the two players and may be next door to one of them. The number a player cares
    /// about, and the number any later prediction would have to be tuned against, is the time to the
    /// person they are playing with, and only an end-to-end exchange measures that.
    ///
    /// One message serves both directions: a receiver sends the same token back with
    /// <see cref="IsReply"/> set, and the original sender matches it against when it sent that token.
    /// Nothing but the sender needs to understand the token, so nothing else has to agree on its meaning.
    /// </summary>
    public struct PingMessage : INetMessage
    {
        /// <summary>Who sent this, so a reply can be attributed to the right player.</summary>
        public PlayerId Sender;

        /// <summary>
        /// Identifies this exchange. Echoed back untouched; only the sender interprets it.
        /// </summary>
        public uint Token;

        /// <summary>
        /// False on the way out, true on the way back. Without it a reply would look like a fresh ping and
        /// the two sides would answer each other forever.
        /// </summary>
        public bool IsReply;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Sender.Value);
            writer.Put(Token);
            writer.Put(IsReply);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Sender = new PlayerId(reader.GetByte());
            Token = reader.GetUInt();
            IsReply = reader.GetBool();
        }
    }
}
