using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A creature's dirt throw hit a player's body on the host, so that player's own screen gets the dirt.
    /// </summary>
    public struct DirtBlindMessage : INetMessage
    {
        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
        }
    }
}
