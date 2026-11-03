using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A teammate used the help-up interaction on a downed player's body. Only the downed player's machine can stand them up.
    /// </summary>
    public struct HelpUpMessage : INetMessage
    {
        /// <summary>Who is being pulled up.</summary>
        public PlayerId Downed;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Downed.Value);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Downed = new PlayerId(reader.GetByte());
        }
    }
}
