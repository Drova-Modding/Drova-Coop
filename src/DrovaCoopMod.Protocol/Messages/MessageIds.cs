namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The wire contract: every message type this mod sends, and the id it travels under. Ids are what
    /// both ends agree on, not registration order, so they are declared once here and never reused for a
    /// different type - an id that changes meaning between two mod versions is a corrupted packet that
    /// still passes every check.
    ///
    /// Ids 0 to 255 are available, and that space is process-global and shared with every other mod that
    /// uses the API's transport. Leave gaps rather than renumbering.
    /// </summary>
    public static class MessageIds
    {
        /// <summary>
        /// Client to host, first thing after joining: who this is and what version they run.
        /// </summary>
        public const ushort Handshake = 1;

        /// <summary>
        /// Host to client, the verdict on that handshake and, when accepted, the id the client will be
        /// known by for the rest of the session.
        /// </summary>
        public const ushort HandshakeResult = 2;

        /// <summary>
        /// Host to one client, right after accepting it: everyone already in the session. Without this a
        /// joining player only ever knows about the host.
        /// </summary>
        public const ushort RosterSnapshot = 3;

        /// <summary>
        /// Host to everyone else: a player joined.
        /// </summary>
        public const ushort PlayerJoined = 4;

        /// <summary>
        /// Host to everyone else: a player left.
        /// </summary>
        public const ushort PlayerLeft = 5;

        /// <summary>
        /// Host to everyone: the session rules changed. See <see cref="SessionRulesMessage"/>.
        /// </summary>
        public const ushort SessionRules = 6;

        /// <summary>
        /// Where a player is. Sent continuously, so it is unreliable by design.
        /// </summary>
        public const ushort PlayerState = 10;

        /// <summary>
        /// Round-trip measurement, in both directions. See <see cref="PingMessage"/>.
        /// </summary>
        public const ushort Ping = 11;

        /// <summary>
        /// What a player has equipped. Sent on change rather than continuously, and therefore reliable.
        /// </summary>
        public const ushort PlayerEquipment = 12;

        /// <summary>
        /// A blow one player landed on another. See <see cref="DamageReportMessage"/>. Sent by the
        /// attacker and addressed to the victim; the host routes it when the two are both clients.
        /// </summary>
        public const ushort DamageReport = 13;

        /// <summary>
        /// That a player started or stopped using an ability. See <see cref="ItemUseMessage"/>. Sent
        /// on the edges rather than continuously, and therefore reliable.
        /// </summary>
        public const ushort ItemUse = 14;

        /// <summary>
        /// A status effect one player's machine put on another player's body. See
        /// <see cref="StatusEffectMessage"/>. Sent by whoever applied it and addressed to the player it
        /// belongs on; the host routes it when the two are both clients.
        /// </summary>
        public const ushort StatusEffect = 15;

        /// <summary>
        /// Something a player learned that everyone should know - a recipe, a letter. See
        /// <see cref="KnowledgeMessage"/>. Travels in both directions and reaches everybody, unlike the
        /// surrounding messages.
        /// </summary>
        public const ushort Knowledge = 16;

        /// <summary>
        /// What a creature was worth to whoever killed it, so everybody is paid for it. See
        /// <see cref="ExperienceMessage"/>. Travels in both directions and reaches everybody, like
        /// <see cref="Knowledge"/> and for the same reason.
        /// </summary>
        public const ushort Experience = 17;

        /// <summary>
        /// That something which is not a player died. See <see cref="EnemyDeathMessage"/>. Travels in both
        /// directions and reaches everybody.
        ///
        /// A fresh decade rather than the next free id. Everything below this is about players, and
        /// the gaps in that range are held for the rest of what players will need; the world's own state
        /// starts here so a reader can tell which half of the protocol a number belongs to without a
        /// lookup.
        /// </summary>
        public const ushort EnemyDeath = 20;

        /// <summary>
        /// Host to one client: this creature is mine now, and here is the handle for it. See
        /// <see cref="EnemyBindMessage"/>.
        /// </summary>
        public const ushort EnemyBind = 21;

        /// <summary>
        /// Host to one client: this creature is yours again. See <see cref="EnemyReleaseMessage"/>.
        /// </summary>
        public const ushort EnemyRelease = 22;

        /// <summary>
        /// Client to host: a blow the local player landed on a creature the host owns. See
        /// <see cref="EnemyDamageMessage"/>.
        /// </summary>
        public const ushort EnemyDamage = 23;

        /// <summary>
        /// Host to everyone: what a creature's health is. See <see cref="EnemyHealthMessage"/>.
        /// </summary>
        public const ushort EnemyHealth = 24;

        /// <summary>
        /// Host to one client, on joining: everything already dead. See
        /// <see cref="EnemyGraveyardMessage"/>.
        /// </summary>
        public const ushort EnemyGraveyard = 27;

        /// <summary>
        /// Host to everyone: where the creatures it drives are and what they are doing. Sent continuously,
        /// so it is unreliable by design. See <see cref="EnemySnapshotMessage"/>.
        /// </summary>
        public const ushort EnemySnapshot = 25;

        /// <summary>
        /// Client to host: which of these creatures has your world already killed? See
        /// <see cref="WorldStateRequestMessage"/>. The answer comes back as a
        /// <see cref="EnemyGraveyardMessage"/>, which is what the client already knows how to apply.
        /// </summary>
        public const ushort WorldStateRequest = 26;

        /// <summary>
        /// Host to everyone: a creature was put into the world at runtime, and here is what to build it
        /// from. See <see cref="EnemySpawnMessage"/>.
        /// </summary>
        public const ushort EnemySpawn = 28;

        /// <summary>
        /// Host to everyone: which handle a conjured creature answers to, named by the cast that produced
        /// it. See <see cref="SummonBindMessage"/>. Unlike <see cref="EnemySpawn"/> this asks nobody to
        /// build anything - the far side already did, because the cast itself was replicated.
        /// </summary>
        public const ushort SummonBind = 29;
    }
}
