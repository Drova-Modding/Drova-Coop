using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's clock, so every world lives through the same day.
    ///
    /// Each machine's time advances on its own, and sleeping jumps it by hours - so two worlds drift
    /// into different days, and day-staged story content diverges with them. The host's total game
    /// seconds cross periodically; a client nudges its clock silently for drift and sleeps along,
    /// with the game's own day-forward semantics, when the host's time leaps.
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
