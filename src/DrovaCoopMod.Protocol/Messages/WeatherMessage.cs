using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's weather, so every machine stands under the same sky.
    ///
    /// Each machine's weather system rolls its own dice every in-game hour, so left alone two worlds
    /// drift apart within minutes. The whole of a weather state is five parameters; the host sends its
    /// current ones, and a client freezes its own weather decisions and follows.
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
