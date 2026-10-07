using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Whether the host moves one spawned creature's body.
    /// Spawned creatures cannot be re-bound because their guid is local to the machine that built them.
    /// </summary>
    public struct EnemyDriveMessage : INetMessage
    {
        /// <summary>The creature.</summary>
        public EnemyNetId NetId;

        /// <summary>Whether the host moves its body. Its health is the host's either way.</summary>
        public bool HostDrives;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(NetId.Value);
            writer.Put(HostDrives);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());
            HostDrives = reader.GetBool();
        }
    }
}
