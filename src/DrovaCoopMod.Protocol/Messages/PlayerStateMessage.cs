using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a player is trying to do, and where they were when they said so.
    ///
    /// **Intent, not just transforms.** The direction and mode are exactly what the local input layer
    /// feeds the game's own movement and look modules, so handing them to a remote body drives the same
    /// code that drives the real player: it faces the right way and plays the right animation without any
    /// of that being decided here. A position alone carries none of that - it says where someone ended up
    /// and nothing about what they were doing, leaving the receiver to infer a facing and an animation
    /// from successive coordinates, which is guesswork that disagrees with the game the moment a player
    /// backs away from something while still looking at it.
    ///
    /// The position rides along because intent alone does not determine where a body ends up: the two
    /// machines are running the same instructions from different starting points, over a link that loses
    /// packets. The sender is the authority on where it actually is.
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
