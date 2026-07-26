using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Where a player is and which way they face. The placeholder the rest of the sync work grows out of,
    /// and the shape to copy: only blittable fields, so sending and receiving it allocates nothing even at
    /// one message per frame per player.
    ///
    /// Send it <see cref="Delivery.Unreliable"/>. Losing one of these costs nothing, because the next one
    /// supersedes it, and paying for reliable delivery of superseded data is how a coop mod spends its
    /// bandwidth on nothing.
    /// </summary>
    public struct PlayerStateMessage : INetMessage
    {
        /// <summary>
        /// Who this describes. The host fills it in when relaying a client's state to the others, because
        /// only the host knows every peer.
        /// </summary>
        public int PlayerId;

        /// <summary>World position.</summary>
        public float X;

        /// <summary>World position.</summary>
        public float Y;

        /// <summary>Facing angle in degrees.</summary>
        public float Facing;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(PlayerId);
            writer.Put(X);
            writer.Put(Y);
            writer.Put(Facing);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            PlayerId = reader.GetInt();
            X = reader.GetFloat();
            Y = reader.GetFloat();
            Facing = reader.GetFloat();
        }
    }
}
