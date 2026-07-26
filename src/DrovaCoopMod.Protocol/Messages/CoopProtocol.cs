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
        /// </summary>
        public const ushort Version = 1;
    }
}
