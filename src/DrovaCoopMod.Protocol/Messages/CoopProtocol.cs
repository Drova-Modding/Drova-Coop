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
        /// **Before the first release this is a rebuild tripwire, not a compatibility contract.** Nothing
        /// outside this repository has ever spoken an older version, so there is no lineage to document
        /// and no peer to stay compatible with - the only thing the number catches is a headless peer or
        /// a second install built before the last layout change, which it catches by differing at all.
        /// What each version added is in the file history, where it cannot go stale. A real ledger starts
        /// at the first public build, when a version number first describes something a stranger is
        /// running.
        ///
        /// Not to be confused with the relay's own wire version, which belongs to the API and the relay
        /// server and moves independently. Conflating the two in a log line or an error message sends
        /// whoever reads it looking in the wrong repository.
        /// </summary>
        public const ushort Version = 9;

        /// <summary>
        /// The most clients a host will accept, so ids and roster messages have a bound. Two players is
        /// what the design targets; the headroom exists so four does not need a protocol change.
        /// </summary>
        public const int MaxClients = 3;
    }
}
