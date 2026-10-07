using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Client to host: send me the world's variables again, after a load rewound this machine's world.
    /// Safe to answer more than once, since a catch-up asserts values.
    /// </summary>
    public struct GVarResyncRequestMessage : INetMessage
    {
        /// <summary>The client loaded a savegame, so its world went backwards.</summary>
        public const byte ReasonSavegameLoaded = 0;

        /// <summary>A player asked for it with <c>coop_resync</c>.</summary>
        public const byte ReasonManual = 1;

        /// <summary>Why the client wants the world again. For the host's log line only.</summary>
        public byte Reason;

        /// <summary>
        /// Whether to also fire change events for values the client already holds, to repair saves whose
        /// consequences never ran.
        /// </summary>
        public byte ReplayUnchanged;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Reason);
            writer.Put(ReplayUnchanged);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Reason = reader.GetByte();
            ReplayUnchanged = reader.GetByte();
        }
    }
}
