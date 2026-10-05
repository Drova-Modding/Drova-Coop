using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host's word that the run is lost: every player is down from a killing blow, or one of them is dead.
    /// Host to everyone; each machine then goes back to <see cref="Checkpoint"/> if it wrote that one, and to its own
    /// latest save otherwise.
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
