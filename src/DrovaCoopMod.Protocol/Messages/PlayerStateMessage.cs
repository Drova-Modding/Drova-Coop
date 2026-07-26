using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a player is trying to do, and where they were when they said so.
    ///
    /// **Intent, not transforms.** The direction and mode are what the local input layer feeds the game's
    /// own movement module, so replaying them on a remote body makes the game animate, collide, play
    /// footsteps and spend stamina by itself. Replicating positions instead means reimplementing all of
    /// that badly, which is the mistake every surveyed project that skipped this had to undo.
    ///
    /// The position rides along anyway, but only as a correction: two machines running the same intent
    /// drift, and the sender is the authority on where it actually ended up.
    ///
    /// Send it <see cref="Delivery.Unreliable"/>. Losing one costs nothing, because the next supersedes
    /// it, and paying for reliable delivery of superseded data is how a coop mod spends its bandwidth on
    /// nothing.
    /// </summary>
    public struct PlayerStateMessage : INetMessage
    {
        /// <summary>
        /// Who this describes.
        ///
        /// A sender fills in its own, but the host does not take its word for it: on arrival the host
        /// replaces this with the id it assigned that peer before passing it on. Otherwise any client
        /// could describe any other player, which is the cheapest possible way to move somebody else
        /// around. Clients do trust what the host stamps, because the host is the authority on identity.
        /// </summary>
        public PlayerId PlayerId;

        /// <summary>Where the sender was, for correcting drift rather than for moving the body.</summary>
        public float X;

        /// <inheritdoc cref="X"/>
        public float Y;

        /// <summary>
        /// The direction being pushed, as handed to <c>IMovementModule.SetDirection</c>. Zero means
        /// standing still.
        /// </summary>
        public float DirectionX;

        /// <inheritdoc cref="DirectionX"/>
        public float DirectionY;

        /// <summary>
        /// Where the player is looking, as handed to <c>ILookModule.SetLookDirection</c>. Separate from
        /// the movement direction because Drova lets a player back away while still facing a threat.
        /// </summary>
        public float LookX;

        /// <inheritdoc cref="LookX"/>
        public float LookY;

        /// <summary>
        /// The intended move mode - stand, walk or run - as <c>Actor.EMoveMode</c>. Sent as a byte
        /// because the enum's numbering is the game's, and a value this build does not know is better
        /// clamped by the receiver than trusted.
        /// </summary>
        public byte MoveMode;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(PlayerId.Value);
            writer.Put(X);
            writer.Put(Y);
            writer.Put(DirectionX);
            writer.Put(DirectionY);
            writer.Put(LookX);
            writer.Put(LookY);
            writer.Put(MoveMode);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            PlayerId = new PlayerId(reader.GetByte());
            X = reader.GetFloat();
            Y = reader.GetFloat();
            DirectionX = reader.GetFloat();
            DirectionY = reader.GetFloat();
            LookX = reader.GetFloat();
            LookY = reader.GetFloat();
            MoveMode = reader.GetByte();
        }
    }
}
