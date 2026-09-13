using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Where a player is and what they are doing, thirty times a second. The hottest message there is,
    /// so it carries only what changes at that rate and carries it small: twenty bytes of payload.
    ///
    /// Directions are angles in one byte each, a 256th of a turn apart, which is finer than anything a
    /// body's facing shows. A direction that is standing still has no angle, so whether the player is
    /// moving is a flag rather than a zero vector. Position stays as two floats, because it is the one
    /// number here that must not be rounded: the far body is put exactly where this one was.
    ///
    /// What a player's maximum health and stamina are changes when they level or equip, not when they
    /// move, so it rides <see cref="PlayerVitalsMessage"/> on change instead of here every tick.
    /// </summary>
    public struct PlayerStateMessage : INetMessage
    {
        /// <summary>Set when the player is moving, so <see cref="MoveAngle"/> means something.</summary>
        public const byte FlagMoving = 1;

        /// <summary>Set when the current action's button was let go. See <see cref="ActionReleased"/>.</summary>
        public const byte FlagActionReleased = 2;

        /// <summary>Set while the player is down. See <see cref="Downed"/>.</summary>
        public const byte FlagDowned = 4;

        /// <summary>
        /// The furthest a coordinate may be from the origin and still be believed. Well outside any map,
        /// and well inside where float precision would start to hurt.
        /// </summary>
        public const float MaxCoordinate = 1_000_000f;

        /// <summary>Who this is. The host overwrites it with the id it handed the sending connection.</summary>
        public PlayerId PlayerId;

        /// <summary>World position.</summary>
        public float X;

        /// <summary>World position.</summary>
        public float Y;

        /// <summary>
        /// The direction the player is moving in, as a 256th of a turn counterclockwise from +X. Only
        /// meaningful while <see cref="Moving"/>.
        /// </summary>
        public byte MoveAngle;

        /// <summary>The direction the player is facing, encoded like <see cref="MoveAngle"/>.</summary>
        public byte LookAngle;

        /// <summary>The flag bits: moving, released, downed.</summary>
        public byte Flags;

        /// <summary>The game's movement mode: standing, walking, running, sneaking.</summary>
        public byte MoveMode;

        /// <summary>The game's interaction mode, which is what holds or stows the weapon.</summary>
        public byte InteractionMode;

        /// <summary>The action being performed, as the bind index the receiving build resolves.</summary>
        public byte Action;

        /// <summary>
        /// Counts up once per new use of an action, so a repeat of the same bind is told from the one
        /// still running, and a late packet cannot restart a swing that finished.
        /// </summary>
        public byte ActionSequence;

        /// <summary>Current health, capped to the wire.</summary>
        public ushort Health;

        /// <summary>Current stamina, capped to the wire.</summary>
        public ushort Stamina;

        /// <summary>Whether the player is moving. When false the move angle is not to be read.</summary>
        public bool Moving
        {
            readonly get => (Flags & FlagMoving) != 0;
            set => Flags = value ? (byte)(Flags | FlagMoving) : (byte)(Flags & ~FlagMoving);
        }

        /// <summary>
        /// Whether the button behind <see cref="Action"/> has been let go, which is what ends a held
        /// attack or a charged one.
        /// </summary>
        public bool ActionReleased
        {
            readonly get => (Flags & FlagActionReleased) != 0;
            set => Flags = value ? (byte)(Flags | FlagActionReleased) : (byte)(Flags & ~FlagActionReleased);
        }

        /// <summary>Whether the player is on the floor. A message that never said reads as standing.</summary>
        public bool Downed
        {
            readonly get => (Flags & FlagDowned) != 0;
            set => Flags = value ? (byte)(Flags | FlagDowned) : (byte)(Flags & ~FlagDowned);
        }

        /// <summary>
        /// Whether an action sequence number is newer than the one already applied, allowing for the
        /// byte wrapping around.
        /// </summary>
        public static bool IsNewerAction(byte arrived, byte applied)
        {
            byte delta = unchecked((byte)(arrived - applied));
            return delta != 0 && delta <= 127;
        }

        /// <summary>
        /// A direction as one byte: 256ths of a turn counterclockwise from +X. A direction too short to
        /// have one is reported as such, so the caller can carry that as a flag instead of a zero.
        /// </summary>
        /// <param name="x">The direction's X.</param>
        /// <param name="y">The direction's Y.</param>
        /// <param name="angle">The packed angle, or zero when there is none.</param>
        /// <returns>Whether the direction was long enough to have an angle.</returns>
        public static bool TryPackAngle(float x, float y, out byte angle)
        {
            angle = 0;

            if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
            if (x * x + y * y < 0.0001f) return false;

            double turns = Math.Atan2(y, x) / (Math.PI * 2d);
            if (turns < 0d) turns += 1d;

            angle = (byte)((int)Math.Round(turns * 256d) & 0xFF);
            return true;
        }

        /// <summary>
        /// The unit direction a packed angle stands for.
        /// </summary>
        public static void UnpackAngle(byte angle, out float x, out float y)
        {
            double radians = angle / 256d * Math.PI * 2d;

            x = (float)Math.Cos(radians);
            y = (float)Math.Sin(radians);
        }

        /// <summary>
        /// Whether the position could belong to a player. NaN is the one that matters: it reaches the
        /// body's transform and then feeds the next frame's interpolation as its own start, so one NaN
        /// leaves the body broken for the rest of the session.
        /// </summary>
        public readonly bool IsPlausible()
        {
            return Within(X) && Within(Y);
        }

        private static bool Within(float value)
        {
            return float.IsFinite(value) && value >= -MaxCoordinate && value <= MaxCoordinate;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(PlayerId.Value);
            writer.Put(X);
            writer.Put(Y);
            writer.Put(MoveAngle);
            writer.Put(LookAngle);
            writer.Put(Flags);
            writer.Put(MoveMode);
            writer.Put(InteractionMode);
            writer.Put(Action);
            writer.Put(ActionSequence);
            writer.Put(Health);
            writer.Put(Stamina);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            PlayerId = new PlayerId(reader.GetByte());
            X = reader.GetFloat();
            Y = reader.GetFloat();
            MoveAngle = reader.GetByte();
            LookAngle = reader.GetByte();
            Flags = reader.GetByte();
            MoveMode = reader.GetByte();
            InteractionMode = reader.GetByte();
            Action = reader.GetByte();
            ActionSequence = reader.GetByte();
            Health = reader.GetUShort();
            Stamina = reader.GetUShort();
        }
    }
}
