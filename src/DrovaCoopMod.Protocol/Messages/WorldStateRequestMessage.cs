using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A client asking the host which of these creatures its world has already killed.
    ///
    /// The client plays in the host's world, not its own. Two people's saves disagree about what is
    /// dead, and that disagreement cannot be settled after a body has loaded: death latches and nothing
    /// undoes it, so a creature dead on one machine and alive on the other stays that way. The way out is
    /// to stop the client's own save from having an opinion - it loads everything alive - and to let the
    /// host say what is dead, because killing is a correction that can always be applied.
    ///
    /// Asked in batches as bodies stream in, rather than answered up front, because a save's worth of dead
    /// creatures is not something either end can enumerate cheaply: the game answers the question one guid
    /// at a time out of a dictionary, and only the guids this player is actually walking past matter.
    /// </summary>
    public struct WorldStateRequestMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>
        /// How many guids fit in one request. Sized so a full batch stays inside a datagram, and small
        /// enough that a client streaming a whole region sends several rather than one enormous one.
        /// </summary>
        public const int MaxGuids = WireStrings.MaxGuids;

        /// <summary>Which creatures the client wants a verdict on. Never null after a read.</summary>
        public string[] Guids;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            WireStrings.PutStrings(writer, Guids, MaxGuids, MaxGuidLength);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            // Bounded on read as well as on write: the count decides how much the host allocates and how
            // many save lookups it performs on behalf of a stranger.
            Guids = WireStrings.GetStrings(reader, MaxGuids, MaxGuidLength);
        }
    }
}
