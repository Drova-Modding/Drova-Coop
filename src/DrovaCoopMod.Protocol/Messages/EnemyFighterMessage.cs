using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to one client: whether that client's machine thinks for a creature it is fighting.
    /// Everyone else keeps following the host, which mirrors the fighter's copy.
    /// </summary>
    public struct EnemyFighterMessage : INetMessage
    {
        /// <summary>The creature.</summary>
        public EnemyNetId NetId;

        /// <summary>True hands the creature's AI to the receiver, false takes it back.</summary>
        public bool Yours;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(NetId.Value);
            writer.Put(Yours);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());
            Yours = reader.GetBool();
        }
    }
}
