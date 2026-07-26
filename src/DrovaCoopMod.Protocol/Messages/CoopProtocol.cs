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
        /// The coop protocol version. Bump it whenever a message layout changes; two mods that disagree
        /// here refuse each other at the handshake instead of desyncing later for no visible reason.
        ///
        /// Not to be confused with the relay's own wire version, which belongs to the API and the relay
        /// server and moves independently. Conflating the two in a log line or an error message sends
        /// whoever reads it looking in the wrong repository.
        ///
        /// Version 2 added session-wide player ids and the roster messages. Version 1 peers were refused
        /// rather than tolerated: they keyed players by transport peer id, which means something
        /// different on every machine.
        ///
        /// Version 3 replaced the position-and-facing state with movement intent, so a remote body is
        /// driven through the game's own movement module instead of having its transform written.
        ///
        /// Version 4 added the mod-set hash to the handshake and an end-to-end ping.
        /// </summary>
        public const ushort Version = 4;

        /// <summary>
        /// The most clients a host will accept, so ids and roster messages have a bound. Two players is
        /// what the design targets; the headroom exists so four does not need a protocol change.
        /// </summary>
        public const int MaxClients = 3;
    }
}
