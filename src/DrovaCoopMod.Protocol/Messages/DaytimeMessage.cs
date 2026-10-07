using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's clock, so every world lives through the same day. Sent periodically.
    /// </summary>
    public struct DaytimeMessage : INetMessage
    {
        /// <summary>The host's time as total game seconds since day zero.</summary>
        public double TotalSeconds;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(TotalSeconds);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            TotalSeconds = reader.GetDouble();
        }
    }
}
