using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's weather parameters. Each machine rolls weather hourly, so clients freeze theirs and follow.
    /// </summary>
    public struct WeatherMessage : INetMessage
    {
        /// <summary>How many parameters a weather state carries. The game's own figure.</summary>
        public const int ParamCount = 5;

        /// <summary>The parameter values, in the game's own order.</summary>
        public float[] Params;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            for (int index = 0; index < ParamCount; index++)
            {
                writer.Put(Params != null && index < Params.Length ? Params[index] : 0f);
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Params = new float[ParamCount];

            for (int index = 0; index < ParamCount; index++)
            {
                Params[index] = reader.GetFloat();
            }
        }
    }
}
