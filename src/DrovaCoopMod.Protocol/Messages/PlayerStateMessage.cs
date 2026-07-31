using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a player is trying to do, and where they were when they said so.
    ///
    /// Intent, not just transforms. The direction and mode are exactly what the local input layer
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
        /// This is what decides whether an attack is made with a weapon. Having the spear equipped is
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
        /// Letting go and finishing are different events, and treating the end of the action as the
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
        /// How much health the sender has left.
        ///
        /// The resulting value, never the damage. A delta would have to be turned back into a health
        /// figure by the receiver, and the game does that through the defender's own stats, damage
        /// blockers, invincibility and crit roll - all of which belong to the machine doing the
        /// calculating and none of which match across two of them. Sending what the number ended up as
        /// leaves nothing to recompute and therefore nothing to disagree about.
        ///
        /// It rides the continuous stream rather than going out as an event on change, for the same
        /// reason <see cref="ActionReleased"/> does: it is a level, so a lost packet is repaired by the
        /// next one instead of leaving a body permanently wrong. An event would need reliable delivery to
        /// be safe and would still be wrong for a whole tick after a missed one.
        /// </summary>
        public ushort Health;

        /// <summary>
        /// The sender's maximum health, so the far end can render a fraction rather than a bare number.
        ///
        /// Sent alongside rather than once, despite changing very rarely. It is two bytes on a packet
        /// that already exists, against a separate reliable message and a rule for when to resend it -
        /// and a maximum that arrives late leaves every health bar in the session wrong in the meantime.
        /// </summary>
        public ushort MaxHealth;

        /// <summary>
        /// How much stamina the sender has left, rounded to whole points.
        ///
        /// This is what decides whether a block holds. Drova resolves a block through
        /// <c>AA_BlockAction.CalcDamageAfterStamina</c>: enough stamina and the damage becomes zero, not
        /// enough and the guard breaks, the poise is crushed and the rest comes through. A remote body
        /// with the wrong stamina computes the wrong one of those, so the machine watching sees a guard
        /// that never breaks while its owner's already has.
        ///
        /// A level on the continuous stream, for the same reason health is one. It also has to be, given
        /// what it replaces: a body whose stamina came from its own simulation would drift a little on
        /// every action and be wrong in a way nothing repairs.
        /// </summary>
        public ushort Stamina;

        /// <summary>
        /// The sender's maximum stamina. Carried alongside because the block calculation is against a
        /// fraction of the maximum - <c>DamageCalculator.CalcStaminaDmg</c> takes it as an argument - so
        /// the current value on its own does not answer the question it is sent to answer.
        /// </summary>
        public ushort MaxStamina;

        /// <summary>
        /// Non-zero while the sender is on the floor, having been saved from a killing blow.
        ///
        /// Replicated as a state, not inferred from the stance. The stance byte moves into
        /// <c>KnockOut</c> along with everything else and looks like it would be enough, but it only puts
        /// the body in the pose: none of the rest of a knockout - actions cleared, AI stopped, hit
        /// receivers down - comes with it, and getting up again depends on a stance packet winning a race
        /// against everything else driving that module. Sending the fact and letting each machine call
        /// <c>BrawlActor.Knockout</c> and <c>RemoveKnockout</c> means both ends run the game's own
        /// mechanic instead of one end running an imitation.
        ///
        /// A level for the same reason <see cref="ActionReleased"/> is one. Being down lasts seconds,
        /// which is many packets; an event saying "went down" could be lost and leave a body on the floor
        /// for the rest of the session, while a flag is repaired by the very next packet.
        /// </summary>
        public byte Downed;

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

        /// <summary>
        /// The furthest from the origin a coordinate may claim to be. Not a guess at how big the world
        /// is - it is a bound well below the range where single-precision arithmetic stops being useful,
        /// chosen to exclude the absurd rather than to police the plausible.
        /// </summary>
        public const float MaxCoordinate = 1_000_000f;

        /// <summary>
        /// The largest a direction or look component may be. These come off an input axis, so anything
        /// past a couple of units already means nothing; the limit is loose for the same reason
        /// <see cref="MaxCoordinate"/> is.
        /// </summary>
        public const float MaxAxis = 1_000f;

        /// <summary>
        /// Whether every number in here is one a body can actually be given.
        ///
        /// The bytes are each checked where they are used - the move mode, the stance and the action are
        /// all matched against known values and ignored otherwise - but the floats used to reach the
        /// engine exactly as they arrived, and NaN is the case that matters. A NaN position reaches
        /// <c>MovementModule.SetPosition</c>, lands in the body's transform, and is read back as the
        /// starting point of the next frame's interpolation; from then on every result is NaN as well. The
        /// body stays broken after the packets that broke it have stopped, because the arithmetic that
        /// would repair it is consuming its own poisoned output. Infinity arrives at the same place one
        /// frame later, and a merely enormous coordinate throws the body somewhere nobody can follow.
        ///
        /// The whole message is refused rather than having its numbers repaired. Substituting a value
        /// would invent a position the sender never described and pair it with intent that no longer
        /// matches it. Dropping costs nothing: these are unreliable and superseded by design, so the next
        /// one puts the body right.
        /// </summary>
        public readonly bool IsPlausible()
        {
            return Within(X, MaxCoordinate)
                && Within(Y, MaxCoordinate)
                && Within(DirectionX, MaxAxis)
                && Within(DirectionY, MaxAxis)
                && Within(LookX, MaxAxis)
                && Within(LookY, MaxAxis);
        }

        // IsFinite is false for NaN and for both infinities, which is most of the test; the range check is
        // what is left for values that are finite and still nonsense.
        private static bool Within(float value, float limit)
        {
            return float.IsFinite(value) && value >= -limit && value <= limit;
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
            writer.Put(Health);
            writer.Put(MaxHealth);
            writer.Put(Stamina);
            writer.Put(MaxStamina);
            writer.Put(Downed);
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
            Health = reader.GetUShort();
            MaxHealth = reader.GetUShort();
            Stamina = reader.GetUShort();
            MaxStamina = reader.GetUShort();
            Downed = reader.GetByte();
        }
    }
}
