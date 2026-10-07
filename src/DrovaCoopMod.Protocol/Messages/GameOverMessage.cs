using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's word that the run is lost. Each machine loads <see cref="Checkpoint"/> if it wrote it, else its latest save.
    /// </summary>
    public struct GameOverMessage : INetMessage
    {
        /// <summary>The last checkpoint the host committed this session, or 0 for none.</summary>
        public ushort Checkpoint;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Checkpoint);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Checkpoint = reader.GetUShort();
        }
    }
}
