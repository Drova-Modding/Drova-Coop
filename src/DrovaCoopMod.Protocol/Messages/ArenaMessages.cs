using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The PvP circle the host marked, where friendly fire is on whatever the session rule says. Host to everyone.
    /// </summary>
    public struct ArenaStateMessage : INetMessage
    {
        /// <summary>Smallest radius, in world units.</summary>
        public const float MinRadius = 2f;

        /// <summary>Largest radius, so an arena cannot switch the session rule off for a whole region.</summary>
        public const float MaxRadius = 2000f;

        /// <summary>The radius when none is given.</summary>
        public const float DefaultRadius = 1000f;

        /// <summary>A marked ring.</summary>
        public const byte StateMarked = 1;

        /// <summary>A marked ring with a fight under way in it.</summary>
        public const byte StateFighting = 2;

        /// <summary>0 for no ring, else <see cref="StateMarked"/> or <see cref="StateFighting"/>. When 0 the other fields mean nothing.</summary>
        public byte Active;

        /// <summary>Center X.</summary>
        public float CenterX;

        /// <summary>Center Y.</summary>
        public float CenterY;

        /// <summary>Radius, clamped on read.</summary>
        public float Radius;

        /// <summary>Rounds after which the fight stops by itself, 0 for no limit.</summary>
        public byte RoundLimit;

        /// <summary>Whether the values are finite and in range.</summary>
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
            writer.Put(RoundLimit);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Active = reader.GetByte();
            CenterX = reader.GetFloat();
            CenterY = reader.GetFloat();

            float radius = reader.GetFloat();
            Radius = Math.Clamp(radius, MinRadius, MaxRadius);
            RoundLimit = reader.GetByte();
        }
    }

    /// <summary>
    /// The host's verdict on a round, since each machine only trusts its own player's health. Host to everyone.
    /// </summary>
    public struct ArenaRoundMessage : INetMessage
    {
        /// <summary>Round number, counting from one.</summary>
        public ushort Round;

        /// <summary>A player of the winning side, or none for a draw.</summary>
        public PlayerId Winner;

        /// <summary>Players in the circle when it ended.</summary>
        public byte Fighters;

        /// <summary>Whether this names a round that could have happened.</summary>
        public readonly bool IsPlausible()
        {
            return Round > 0 && Fighters >= 2;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Round);
            writer.Put(Winner.Value);
            writer.Put(Fighters);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Round = reader.GetUShort();
            Winner = new PlayerId(reader.GetByte());
            Fighters = reader.GetByte();
        }
    }

    /// <summary>
    /// A player's arena team, 0 for none. Host to everyone, and a client's request to the host.
    /// </summary>
    public struct ArenaTeamMessage : INetMessage
    {
        /// <summary>Whose team this is.</summary>
        public PlayerId Player;

        /// <summary>The team, 0 for none.</summary>
        public byte Team;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Player.Value);
            writer.Put(Team);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Player = new PlayerId(reader.GetByte());
            Team = reader.GetByte();
        }
    }
}
