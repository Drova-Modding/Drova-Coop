namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Wire ids of every coop message. Never reuse or renumber an id.
    /// Ids 0 to 255 are shared with every other mod on the API's transport.
    /// </summary>
    public static class MessageIds
    {
        /// <summary>Client to host on joining: identity and version.</summary>
        public const ushort Handshake = 1;

        /// <summary>Host to client: the handshake verdict and, when accepted, the client's id.</summary>
        public const ushort HandshakeResult = 2;

        /// <summary>Host to a new client: everyone already in the session.</summary>
        public const ushort RosterSnapshot = 3;

        /// <summary>Host to everyone else: a player joined.</summary>
        public const ushort PlayerJoined = 4;

        /// <summary>Host to everyone else: a player left.</summary>
        public const ushort PlayerLeft = 5;

        /// <summary>Host to everyone: the session rules changed.</summary>
        public const ushort SessionRules = 6;

        /// <summary>A player's position, sent continuously and unreliable.</summary>
        public const ushort PlayerState = 10;

        /// <summary>Round-trip measurement, both directions.</summary>
        public const ushort Ping = 11;

        /// <summary>A player's equipment, sent on change.</summary>
        public const ushort PlayerEquipment = 12;

        /// <summary>A blow one player landed on another, routed by the host between clients.</summary>
        public const ushort DamageReport = 13;

        /// <summary>A player started or stopped using an ability.</summary>
        public const ushort ItemUse = 14;

        /// <summary>A status effect one machine put on another player's body.</summary>
        public const ushort StatusEffect = 15;

        /// <summary>Something a player learned (recipe, letter), shared with everyone.</summary>
        public const ushort Knowledge = 16;

        /// <summary>A kill's experience, paid to everyone.</summary>
        public const ushort Experience = 17;

        /// <summary>A non-player died. World-state ids start at 20.</summary>
        public const ushort EnemyDeath = 20;

        /// <summary>Host to one client: the host now drives this creature.</summary>
        public const ushort EnemyBind = 21;

        /// <summary>Host to one client: this creature is yours again.</summary>
        public const ushort EnemyRelease = 22;

        /// <summary>Client to host: a blow on a creature the host owns.</summary>
        public const ushort EnemyDamage = 23;

        /// <summary>Host to everyone: a creature's health.</summary>
        public const ushort EnemyHealth = 24;

        /// <summary>Host to a new client: everything already dead.</summary>
        public const ushort EnemyGraveyard = 27;

        /// <summary>Host to everyone: positions of the creatures it drives, unreliable.</summary>
        public const ushort EnemySnapshot = 25;

        /// <summary>Client to host: which of these creatures are dead in your world.</summary>
        public const ushort WorldStateRequest = 26;

        /// <summary>Host to everyone: a runtime-spawned creature and what to build it from.</summary>
        public const ushort EnemySpawn = 28;

        /// <summary>Host to everyone: the handle of a conjured creature, named by its cast.</summary>
        public const ushort SummonBind = 29;

        /// <summary>Host to everyone: whether the host moves a spawned creature, whose guid is machine-local.</summary>
        public const ushort EnemyDrive = 30;

        /// <summary>Host to one client: these creatures are alive in my world.</summary>
        public const ushort EnemyAlive = 31;

        /// <summary>Host to everyone: creatures the host revived.</summary>
        public const ushort EnemyRevive = 32;

        /// <summary>A global variable changed, relayed by the host.</summary>
        public const ushort GVarChange = 33;

        /// <summary>Host to a new client: the host's moved global variables.</summary>
        public const ushort GVarSnapshot = 34;

        /// <summary>A shared cutscene started.</summary>
        public const ushort Cutscene = 35;

        /// <summary>The host's weather parameters.</summary>
        public const ushort Weather = 36;

        /// <summary>Client to host: where are these actors in your world.</summary>
        public const ushort NpcPlacementRequest = 37;

        /// <summary>Host to one client: where they stand, unknown and dead included.</summary>
        public const ushort NpcPlacementAnswer = 38;

        /// <summary>Journal entries one player earned.</summary>
        public const ushort JournalEntry = 39;

        /// <summary>A journal topic changed category (completed, failed).</summary>
        public const ushort JournalTopic = 40;

        /// <summary>The host's clock.</summary>
        public const ushort Daytime = 41;

        /// <summary>A map one player unlocked.</summary>
        public const ushort MapUnlock = 42;

        /// <summary>Experience a dialogue granted, paid to everyone else.</summary>
        public const ushort DialogueExp = 43;

        /// <summary>Client to host: my player is talking to or brawling with this NPC of yours.</summary>
        public const ushort EnemyEngage = 44;

        /// <summary>Items a dialogue granted, given to everyone else.</summary>
        public const ushort DialogueItems = 45;

        /// <summary>Host to one client: end of the variable catch-up, so consequences run only after all values are stored.</summary>
        public const ushort GVarSnapshotEnd = 46;

        /// <summary>Client to host: resend the world's variables, since loading a save rewinds them.</summary>
        public const ushort GVarResyncRequest = 47;

        /// <summary>A swing at a resource spot, so it erodes in every world.</summary>
        public const ushort ResourceWork = 48;

        /// <summary>Host to a new client: how far every resource spot has been worked.</summary>
        public const ushort ResourceSpotSnapshot = 49;

        /// <summary>Host to one client: end of the resource spot snapshot.</summary>
        public const ushort ResourceSpotSnapshotEnd = 50;

        /// <summary>A rubble pile left a world (mined or delivered), so it leaves every world once.</summary>
        public const ushort HaulPile = 51;

        /// <summary>Client to host: this player slept. The host's clock report carries the answer.</summary>
        public const ushort DayAdvanceRequest = 52;

        /// <summary>Shared loot taken, so it leaves every world once.</summary>
        public const ushort LootTaken = 53;

        /// <summary>Host to a new client: shared loot already taken this session.</summary>
        public const ushort LootSnapshot = 54;

        /// <summary>Host to one client: end of the loot snapshot.</summary>
        public const ushort LootSnapshotEnd = 55;

        /// <summary>A player's maximum health and stamina, sent on change.</summary>
        public const ushort PlayerVitals = 56;

        /// <summary>One scene object's saved state, such as an opened door.</summary>
        public const ushort WorldObject = 57;

        /// <summary>Host to a new client: every world object that changed this session.</summary>
        public const ushort WorldObjectSnapshot = 58;

        /// <summary>Host to one client: end of the world object snapshot.</summary>
        public const ushort WorldObjectSnapshotEnd = 59;

        /// <summary>The defender's answer to a blow: guard held, broke, or perfect block.</summary>
        public const ushort BlockVerdict = 60;

        /// <summary>Where the party agreed to fight each other.</summary>
        public const ushort ArenaState = 61;

        /// <summary>The host's verdict on an arena round.</summary>
        public const ushort ArenaRound = 62;

        /// <summary>What a breakable cache spilled, since its pickup names are machine-local.</summary>
        public const ushort CacheSpill = 63;

        /// <summary>The talents one player has learned.</summary>
        public const ushort PlayerTalents = 64;

        /// <summary>A cutscene teleported an NPC or sent it to its routine place.</summary>
        public const ushort ActorPort = 65;

        /// <summary>The run is lost and everyone reloads their latest save.</summary>
        public const ushort GameOver = 66;

        /// <summary>A save every machine writes at once, which a lost run goes back to.</summary>
        public const ushort Checkpoint = 67;

        /// <summary>A trade between two players, step by step.</summary>
        public const ushort Trade = 68;

        /// <summary>Which arena team a player fights for.</summary>
        public const ushort ArenaTeam = 69;

        /// <summary>A duel or arena team invite.</summary>
        public const ushort Invite = 70;

        /// <summary>A foglet fog patch woke.</summary>
        public const ushort FogletWake = 71;

        /// <summary>A player's own status effect ended, so their copies drop it.</summary>
        public const ushort StatusEnded = 72;

        /// <summary>A conversation line, shown to others as a speech bubble.</summary>
        public const ushort DialogueLine = 73;

        /// <summary>A creature's trap laid or gone in the machine running its fight.</summary>
        public const ushort Trap = 74;

        /// <summary>A game-arena gate barrier switched in the machine running the fight.</summary>
        public const ushort ArenaGate = 75;

        /// <summary>A creature conjured in a client's leased fight, and the host's handle for its copy.</summary>
        public const ushort LeaseSummon = 76;

        /// <summary>A client's player entered or left a region, so the host rolls its ambushes.</summary>
        public const ushort Region = 77;

        /// <summary>A thunder strike in the host's storm.</summary>
        public const ushort Thunder = 78;

        /// <summary>The host's value of a mod setting that must match on every machine.</summary>
        public const ushort HostSetting = 79;
    }
}
