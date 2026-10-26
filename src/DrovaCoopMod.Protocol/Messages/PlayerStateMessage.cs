using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Where a player is and what they are doing, thirty times a second, in twenty bytes. Directions
    /// are one-byte angles, and position stays as exact floats.
    /// </summary>
    public record struct PlayerStateMessage : INetMessage
    {
        /// <summary>Set when the player is moving, so <see cref="MoveAngle"/> means something.</summary>
        private const byte FlagMoving = 1;

        /// <summary>Set when the current action's button was let go. See <see cref="ActionReleased"/>.</summary>
        public const byte FlagActionReleased = 2;

        /// <summary>Set while the player is down. See <see cref="Downed"/>.</summary>
        private const byte FlagDowned = 4;

        /// <summary>Set when the current use continues the chain before it. See <see cref="ActionCombo"/>.</summary>
        private const byte FlagActionCombo = 8;

        /// <summary>Set on the wire when <see cref="ActionPrefab"/> follows. Derived from it, not set by hand.</summary>
        private const byte FlagActionPrefab = 16;

        /// <summary>Set while the player is out of the fight for good or would be. See <see cref="Out"/>.</summary>
        private const byte FlagOut = 32;

        /// <summary>Set while the player is in a scripted brawl. See <see cref="InBrawl"/>.</summary>
        private const byte FlagInBrawl = 64;

        /// <summary>Set while the player's poise is broken. See <see cref="Stunned"/>.</summary>
        private const byte FlagStunned = 128;

        /// <summary>The furthest a coordinate may be from the origin and still be believed.</summary>
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

        /// <summary>The <c>Flag*</c> bits.</summary>
        public byte Flags;

        /// <summary>
        /// The ActionPrefabIndex name hash of an unbound action such as mining or fishing, or 0.
        /// </summary>
        public uint ActionPrefab;

        /// <summary>The game's movement mode: standing, walking, running, sneaking.</summary>
        public byte MoveMode;

        /// <summary>The game's interaction mode, which is what holds or stows the weapon.</summary>
        public byte InteractionMode;

        /// <summary>The action being performed, as the bind index the receiving build resolves.</summary>
        public byte Action;

        /// <summary>
        /// Counts up once per new use of an action, so a repeat is told from the one still running.
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
        /// Whether the button behind <see cref="Action"/> has been let go, ending a held or charged attack.
        /// </summary>
        public bool ActionReleased
        {
            readonly get => (Flags & FlagActionReleased) != 0;
            set => Flags = value ? (byte)(Flags | FlagActionReleased) : (byte)(Flags & ~FlagActionReleased);
        }

        /// <summary>
        /// Whether this use continues the running combo rather than starting a new attack on the same button.
        /// </summary>
        public bool ActionCombo
        {
            readonly get => (Flags & FlagActionCombo) != 0;
            set => Flags = value ? (byte)(Flags | FlagActionCombo) : (byte)(Flags & ~FlagActionCombo);
        }

        /// <summary>Whether the player is on the floor. A message that never said reads as standing.</summary>
        public bool Downed
        {
            readonly get => (Flags & FlagDowned) != 0;
            set => Flags = value ? (byte)(Flags | FlagDowned) : (byte)(Flags & ~FlagDowned);
        }

        /// <summary>
        /// Whether the player is down from a lethal blow, or dead. A brawl knockout sets only <see cref="Downed"/>,
        /// and dead is told from down by <see cref="Health"/> being zero.
        /// </summary>
        public bool Out
        {
            readonly get => (Flags & FlagOut) != 0;
            set => Flags = value ? (byte)(Flags | FlagOut) : (byte)(Flags & ~FlagOut);
        }

        /// <summary>Whether the player is in a scripted brawl.</summary>
        public bool InBrawl
        {
            readonly get => (Flags & FlagInBrawl) != 0;
            set => Flags = value ? (byte)(Flags | FlagInBrawl) : (byte)(Flags & ~FlagInBrawl);
        }

        /// <summary>Whether the player's poise is broken, so they stagger or lie in a full break.</summary>
        public bool Stunned
        {
            readonly get => (Flags & FlagStunned) != 0;
            set => Flags = value ? (byte)(Flags | FlagStunned) : (byte)(Flags & ~FlagStunned);
        }

        /// <summary>
        /// Whether an action sequence number is newer than the one applied, allowing for byte wraparound.
        /// </summary>
        public static bool IsNewerAction(byte arrived, byte applied)
        {
            byte delta = unchecked((byte)(arrived - applied));
            return delta != 0 && delta <= 127;
        }

        /// <summary>
        /// A direction as one byte: 256ths of a turn counterclockwise from +X.
        /// </summary>
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
        /// Whether the position could belong to a player. One NaN would break the body's interpolation for good.
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
            writer.Put(ActionPrefab != 0 ? (byte)(Flags | FlagActionPrefab) : (byte)(Flags & ~FlagActionPrefab));
            writer.Put(MoveMode);
            writer.Put(InteractionMode);
            writer.Put(Action);
            writer.Put(ActionSequence);
            writer.Put(Health);
            writer.Put(Stamina);

            // Only while such an action runs, so the ordinary packet stays its size.
            if (ActionPrefab != 0) writer.Put(ActionPrefab);
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
            ActionPrefab = (Flags & FlagActionPrefab) != 0 ? reader.GetUInt() : 0u;
            Flags = (byte)(Flags & ~FlagActionPrefab);
        }
    }
}
