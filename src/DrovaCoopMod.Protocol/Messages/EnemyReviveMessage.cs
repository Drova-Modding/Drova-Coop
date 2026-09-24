using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Creatures the host has revived by decree, pushed to everybody in the session.
    ///
    /// The unprompted counterpart of <see cref="EnemyAliveMessage"/>. That one only travels as the
    /// answer to a question, and a client asks each question once. A revive that happens after the
    /// asking would reach nobody until they rejoined, while the host fights creatures every other
    /// machine still shows as corpses.
    ///
    /// It also outranks the graveyard, which an alive answer never does. An answer can cross paths
    /// with a fresher death, so there death wins. A revive is the host deliberately taking a recorded
    /// death back, so here the revive wins.
    /// </summary>
    public struct EnemyReviveMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>
        /// How many guids fit in one chunk. Sized so a full chunk stays well inside a datagram even after
        /// the relay's framing and encryption.
        /// </summary>
        public const int MaxGuids = WireStrings.MaxGuids;

        /// <summary>The revived, by guid. Never null after a read.</summary>
        public string[] Guids;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            WireStrings.PutStrings(writer, Guids, MaxGuids, MaxGuidLength);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            // Bounded on read as well as on write. The count decides how much this machine allocates, so
            // it is not a number the sender gets to choose freely.
            Guids = WireStrings.GetStrings(reader, MaxGuids, MaxGuidLength);
        }
    }
}
