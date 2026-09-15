using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Where the party has agreed to fight each other, and how big it is. Host to everyone, whenever it
    /// changes and whenever the roster grows.
    ///
    /// A place rather than a rule. Friendly fire is a session-wide switch, which is the right shape for
    /// an accident between allies and the wrong shape for a duel: a party that wants to settle something
    /// should not have to leave itself open to every stray swing for the rest of the evening. Inside the
    /// circle the party can hurt each other whatever the session says, and leaving restores the rule
    /// they were playing under.
    ///
    /// A circle on the ground rather than an authored scene, because a mod cannot author one. The host
    /// marks it where they are standing.
    /// </summary>
    public struct ArenaStateMessage : INetMessage
    {
        /// <summary>The smallest arena worth marking, in world units.</summary>
        public const float MinRadius = 2f;

        /// <summary>
        /// The largest. An arena is a place two people agree to stand in, and one the size of a region
        /// would turn the session rule off by the back door.
        /// </summary>
        public const float MaxRadius = 200f;

        /// <summary>Whether an arena is marked at all. When false the rest means nothing.</summary>
        public byte Active;

        /// <summary>Where its middle is.</summary>
        public float CenterX;

        /// <summary>Where its middle is.</summary>
        public float CenterY;

        /// <summary>How far it reaches, clamped to the bounds above on read.</summary>
        public float Radius;

        /// <summary>Whether this describes a place a body could actually stand in.</summary>
        public readonly bool IsPlausible()
        {
            if (Active == 0) return true;

            return float.IsFinite(CenterX) && float.IsFinite(CenterY)
                && float.IsFinite(Radius) && Radius >= MinRadius && Radius <= MaxRadius;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Active);
            writer.Put(CenterX);
            writer.Put(CenterY);
            writer.Put(Radius);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Active = reader.GetByte();
            CenterX = reader.GetFloat();
            CenterY = reader.GetFloat();

            float radius = reader.GetFloat();
            Radius = radius < MinRadius ? MinRadius : (radius > MaxRadius ? MaxRadius : radius);
        }
    }

    /// <summary>
    /// The host's word on how a round ended. Host to everyone, once per round.
    ///
    /// The one thing the rest of this protocol cannot answer on its own. Every machine knows its own
    /// player's health and takes everybody else's on trust, which is fine for a fight between friends
    /// and not fine for a contest whose outcome someone will argue about: two players can each believe
    /// they were the one still standing. So the host arbitrates the match - a small, bounded piece of
    /// authority over the round rather than over the characters, which leaves the per-player ownership
    /// that makes the combat feel good exactly where it is.
    /// </summary>
    public struct ArenaRoundMessage : INetMessage
    {
        /// <summary>Which round this was, counting up from one for the life of the arena.</summary>
        public ushort Round;

        /// <summary>Who went down. The host names them from the state everybody already sends.</summary>
        public PlayerId Loser;

        /// <summary>How many players were in the circle when it ended, the loser included.</summary>
        public byte Fighters;

        /// <summary>Whether this names a round that could have happened.</summary>
        public readonly bool IsPlausible()
        {
            return Round > 0 && Loser.IsAssigned && Fighters >= 2;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Round);
            writer.Put(Loser.Value);
            writer.Put(Fighters);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Round = reader.GetUShort();
            Loser = new PlayerId(reader.GetByte());
            Fighters = reader.GetByte();
        }
    }
}
