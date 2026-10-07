using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A client asking the host to advance the day because their player slept, since a client may not advance its own.
    /// The host validates the ask and its clock report then carries the new day to everyone.
    /// </summary>
    public struct DayAdvanceRequestMessage : INetMessage
    {
        /// <summary>Who asked. Stamped by the host from the peer.</summary>
        public PlayerId Player;

        /// <summary>The wake-up time, in total game seconds since day zero.</summary>
        public double TargetTotalSeconds;

        /// <summary>
        /// A dialogue set the time rather than a bed. Granted as authored, even a small or backward step.
        /// </summary>
        public bool Scripted;

        /// <summary>
        /// Whether the target time could come from this game at all.
        /// </summary>
        public readonly bool IsPlausible()
        {
            if (double.IsNaN(TargetTotalSeconds) || double.IsInfinity(TargetTotalSeconds)) return false;

            return TargetTotalSeconds >= 0d && TargetTotalSeconds < 100_000d * 86_400d;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Player.Value);
            writer.Put(TargetTotalSeconds);
            writer.Put(Scripted);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Player = new PlayerId(reader.GetByte());
            TargetTotalSeconds = reader.GetDouble();
            Scripted = reader.GetBool();
        }
    }
}
