using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to everyone: a thunder strike rolled by the host, so all machines flash together.
    /// </summary>
    public struct ThunderMessage : INetMessage
    {
        /// <summary>Reserved, always 0.</summary>
        public byte Kind;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Kind);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Kind = reader.GetByte();
        }
    }
}
