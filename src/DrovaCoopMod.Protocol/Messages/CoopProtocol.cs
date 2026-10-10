namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Wire protocol facts both ends must agree on, kept outside the session so the headless peer and tests can read them.
    /// </summary>
    public static class CoopProtocol
    {
        /// <summary>
        /// Coop protocol version, checked at the handshake. Bump it when a message layout or a field's meaning changes.
        /// Not the relay's own wire version, which moves independently.
        /// </summary>
        public const ushort Version = 1;

        /// <summary>Health a downed player stands up with, in percent of maximum, when the host sent none.</summary>
        public const byte DefaultRecoveryHealthPercent = 5;

        /// <summary>
        /// Creature toughness in percent of authored health, 100 being vanilla.
        /// Applied by dividing the party's damage, so nothing reaches a savegame.
        /// </summary>
        public const ushort DefaultEnemyHealthPercent = 100;

        /// <summary>Lowest enemy health percent a host may set. Below this a one-point blow would land none.</summary>
        public const ushort MinEnemyHealthPercent = 50;

        /// <summary>Highest enemy health percent a host may set.</summary>
        public const ushort MaxEnemyHealthPercent = 300;

        /// <summary>How hard creatures hit players, in percent of authored damage, 100 being vanilla.</summary>
        public const ushort DefaultEnemyDamagePercent = 100;

        /// <summary>Lowest enemy damage percent a host may set.</summary>
        public const ushort MinEnemyDamagePercent = 50;

        /// <summary>Highest enemy damage percent a host may set.</summary>
        public const ushort MaxEnemyDamagePercent = 300;

        /// <summary>Vanilla loot: every world has its own copy of every chest and pickup. The default.</summary>
        public const byte LootModeInstanced = 0;

        /// <summary>
        /// One world's loot for the party. Only game-filled containers take part, never player stashes or story stowage.
        /// </summary>
        public const byte LootModeShared = 1;

        /// <summary>
        /// Most clients a host accepts (sixteen players with the host). Host-only, so raising it needs no protocol bump.
        /// </summary>
        public const int MaxClients = 15;
    }
}
