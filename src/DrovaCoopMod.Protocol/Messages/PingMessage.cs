using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// End-to-end round trip to another player. The transport's ping only measures the time to the relay.
    /// </summary>
    public struct PingMessage : INetMessage
    {
        /// <summary>Who sent this, so a reply can be attributed to the right player.</summary>
        public PlayerId Sender;

        /// <summary>Identifies this exchange. Echoed back untouched, only the sender interprets it.</summary>
        public uint Token;

        /// <summary>True on the way back, so a reply is not answered again.</summary>
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
