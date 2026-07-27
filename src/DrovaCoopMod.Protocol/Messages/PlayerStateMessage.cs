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

        /// <summary>
        /// The stance the player is in - walking about, weapon drawn, sitting - as
        /// <c>Actor.EInteractionMode</c>.
        ///
        /// **This is what decides whether an attack is made with a weapon.** Having the spear equipped is
        /// not the same as holding it: the weapon is stowed outside combat mode, and a body left in
        /// <c>Normal</c> performs the spear's own attack bare-handed, which is exactly as odd as it
        /// sounds. Equipment says what a player owns; this says what is in their hands.
        /// </summary>
        public byte InteractionMode;

        /// <summary>
        /// What the player is doing right now - swinging, dodging, drinking - as
        /// <c>Actor.ActionModule.EActionBind</c>, and zero for nothing at all. A byte for the same reason
        /// the move mode is one: the numbering belongs to the game.
        ///
        /// A standing value rather than an event. Sending "an attack began" would need every one of those
        /// packets to arrive, which these are not, and a lost one would leave a body mid-swing forever;
        /// sending what the player is doing means a lost packet costs nothing, because the next one says
        /// the same thing. The cost is that an action shorter than the send interval can pass between two
        /// samples unseen - no attack, dodge or drink in this game is anywhere near that short.
        /// </summary>
        public byte Action;

        /// <summary>
        /// Bumped by the sender every time <see cref="Action"/> changes, and otherwise left alone.
        ///
        /// It answers two questions the action byte cannot. Two swings in a row read as the same value,
        /// so without this the second one is invisible - the receiver sees the same action it is already
        /// playing and does nothing. And these arrive unreliable and therefore out of order: a packet
        /// held up by one interval would otherwise restart an action that has already finished, which
        /// looks exactly like the far player attacking twice. Comparing sequences discards it instead.
        ///
        /// Wraps at 256 and is meant to: the comparison is on the difference, so the only thing that
        /// matters is that consecutive values are near each other.
        /// </summary>
        public byte ActionSequence;

        /// <summary>
        /// Non-zero once the player has let go of the button for <see cref="Action"/>.
        ///
        /// **Letting go and finishing are different events**, and treating the end of the action as the
        /// release is what made every ordinary attack arrive as a charged one: a tap releases at once and
        /// the swing runs on by itself, so a body that waits for the swing to end holds the button through
        /// all of it. Charged attacks worked precisely because holding was the one thing being replicated
        /// correctly.
        ///
        /// A level rather than an edge, deliberately. These packets are unreliable and superseded, so a
        /// "released now" event can be lost and leave a body charging forever; a flag that simply says
        /// whether the button is down is repaired by the next packet whatever happened to the last one.
        /// </summary>
        public byte ActionReleased;

        /// <summary>
        /// Whether an arriving <see cref="ActionSequence"/> describes something that has not been acted on
        /// yet, given the last one that was.
        ///
        /// The rule the counter's wrap forces: a difference of nothing is the packet already applied, a
        /// difference in the lower half of the range is the future, and anything above it is the past
        /// arriving late. Acting on the past restarts an action its owner has already finished, which on
        /// screen is the far player attacking twice.
        ///
        /// Lives here rather than beside the body that consumes it because it is a rule about what the
        /// field means, and the field is defined here - and because this way it can be tested without a
        /// game to run it against.
        /// </summary>
        /// <param name="arrived">The sequence just received.</param>
        /// <param name="applied">The sequence last acted on.</param>
        public static bool IsNewerAction(byte arrived, byte applied)
        {
            byte delta = unchecked((byte)(arrived - applied));

            return delta != 0 && delta <= 127;
        }

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
            writer.Put(InteractionMode);
            writer.Put(Action);
            writer.Put(ActionSequence);
            writer.Put(ActionReleased);
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
            InteractionMode = reader.GetByte();
            Action = reader.GetByte();
            ActionSequence = reader.GetByte();
            ActionReleased = reader.GetByte();
        }
    }
}
