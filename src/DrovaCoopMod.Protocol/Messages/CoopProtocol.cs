namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Facts about the coop wire protocol that both ends have to agree on, independent of who is
    /// speaking it.
    ///
    /// This lives beside the messages rather than on the session class because the session is bound to
    /// the game, and things that are not the game need to speak this protocol too - the headless peer
    /// that stands in for a second player, and the tests. A version constant only one side can read is
    /// not a contract.
    /// </summary>
    public static class CoopProtocol
    {
        /// <summary>
        /// The coop protocol version. Bump it whenever a message layout changes, and equally whenever a
        /// field keeps its layout but changes what it means; two peers that disagree here refuse each
        /// other at the handshake instead of desyncing later for no visible reason. A changed meaning is
        /// the more dangerous of the two, because the packets still parse: the pair agrees on every byte
        /// and disagrees about what to do with them, which surfaces as a game bug rather than as a
        /// protocol error.
        ///
        /// Before the first release this is a rebuild tripwire, not a compatibility contract. Nothing
        /// outside this repository has ever spoken an older version, so there is no lineage to document
        /// and no peer to stay compatible with - the only thing the number catches is a headless peer or
        /// a second installation built before the last layout change, which it catches by differing at all.
        /// What each version added is in the file history, where it cannot go stale. A real ledger starts
        /// at the first public build, when a version number first describes something a stranger is
        /// running.
        ///
        /// Not to be confused with the relay's own wire version, which belongs to the API and the relay
        /// server and moves independently. Conflating the two in a log line or an error message sends
        /// whoever reads it looking in the wrong repository.
        /// </summary>
        public const ushort Version = 52;

        /// <summary>
        /// How much health a downed player stands up with, as a percentage of their maximum, when nobody
        /// has said otherwise.
        ///
        /// Lives here because both ends need the same answer when the host's is missing - a peer that
        /// filled a gap with its own idea of a sensible default would be running a different rule than
        /// the session.
        ///
        /// Five is deliberately harsh: being put down is meant to hurt, and a player who stands up nearly
        /// whole has lost only a few seconds. It is close to what the revive blocker leaves on its own,
        /// with the difference that it scales with the character rather than being two points whatever
        /// the maximum - and that it is a number somebody chose, which the host can raise.
        /// </summary>
        public const byte DefaultRecoveryHealthPercent = 5;

        /// <summary>
        /// How tough creatures are, as a percentage of their authored health, when nobody has said
        /// otherwise. A hundred is the game as shipped.
        ///
        /// The number is applied to the party's blows rather than to the creatures: every point of
        /// damage a player lands on a creature is divided by this fraction at the funnel where the game
        /// works the wound out. That is the same fight as a creature with that much more health, and
        /// it leaves the creature's own figures untouched, so nothing of it can reach a savegame.
        /// </summary>
        public const ushort DefaultEnemyHealthPercent = 100;

        /// <summary>
        /// The least a host may set <see cref="DefaultEnemyHealthPercent"/>'s rule to. Half is a party
        /// that wants the game easier; below that a blow that lands one point would need to land none.
        /// </summary>
        public const ushort MinEnemyHealthPercent = 50;

        /// <summary>
        /// The most a host may set the enemy health rule to. Three times the authored health is what a
        /// full party of eight can want; more than that turns every camp into a wall.
        /// </summary>
        public const ushort MaxEnemyHealthPercent = 300;

        /// <summary>
        /// The loot rule as the game ships: every world holds its own copy of every chest and pickup,
        /// and what one player takes is still there for the next. The default, and what a peer that
        /// never said reads as.
        /// </summary>
        public const byte LootModeInstanced = 0;

        /// <summary>
        /// One world's worth of loot for the party: a pickup taken is gone for everyone, and a chest
        /// holds what the last player to open it left. Only containers the game itself filled take
        /// part; a chest a player stores things in, or one the story stows a player's belongings in,
        /// stays that player's own.
        /// </summary>
        public const byte LootModeShared = 1;

        /// <summary>
        /// The most clients a host will accept, so ids and roster messages have a bound. Seven, for
        /// eight players counting the host.
        ///
        /// Two remains the tested configuration and everything in this repository is proven at two.
        /// Eight is what the design must not preclude: the arena wants more than a duel, and a large
        /// session is worth playing for its own sake. Nothing in the sync model had to change for it,
        /// which is not luck - the charge cursor, the haul ledger, the assertion log and the enemy
        /// claim are all idempotent set or monotone operations with no round trip, because a claim
        /// between two players is a lease between eight.
        ///
        /// Deliberately NOT a wire-layout constant, so raising it needs no protocol bump: the host
        /// alone reads it, to hand out ids and to refuse the peer that does not fit.
        /// <see cref="RosterSnapshotMessage.MaxPlayers"/> already bounds a roster at eight and
        /// <see cref="PlayerId"/> is a byte, so neither had to move. Two hosts disagreeing about this
        /// number disagree only about how many people their own session holds.
        ///
        /// What this does not do is make eight players connect. Whatever the transport in front of it
        /// caps a session at is the real limit, and it is lower than this on the public relay - the mod
        /// refusing at its own bound is not the constraint. Streaming is where eight will hurt first:
        /// every remote body holds up its own reference point, so eight players in eight regions is
        /// eight sets of streamed chunks on every machine.
        /// </summary>
        public const int MaxClients = 7;
    }
}
