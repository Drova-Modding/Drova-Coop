using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host giving a creature back to the client as an ordinary local creature. The client also
    /// asserts release every frame from local state, so a lost message cannot leave it unkillable.
    /// </summary>
    public struct EnemyReleaseMessage : INetMessage
    {
        /// <summary>The creature has moved out of the range the host claims.</summary>
        public const byte ReasonOutOfRange = 0;

        /// <summary>The host's chunk for it unloaded.</summary>
        public const byte ReasonHostLostBody = 1;

        /// <summary>Which creature.</summary>
        public EnemyNetId NetId;

        /// <summary>Why. A lost host body also removes the copy a client built for it.</summary>
        public byte Reason;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(NetId.Value);
            writer.Put(Reason);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());
            Reason = reader.GetByte();
        }
    }
}
