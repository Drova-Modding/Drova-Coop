using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Client to host: send me the world's variables again.
    ///
    /// A savegame load inside gameplay rewinds this machine's half of the world - every value goes
    /// back to what that save held - without ending the session, so no join happens and nothing else
    /// would ever put it back. The client would then play on against a world the host has moved past,
    /// silently, which is how a reloaded death used to cost a player their quest log.
    ///
    /// Safe to send at any time and safe to answer more than once: a catch-up asserts values rather
    /// than describing changes, so applying one twice lands on the same world.
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
        /// Whether the host should also re-run the consequences of values this client already holds.
        ///
        /// Normally a catch-up only runs the listeners of values it actually moved. A save written by
        /// an older build can hold the right value with its consequences never run - the state is
        /// there and the quest graph never heard about it - and that is only repairable by firing the
        /// change event on a value that did not change. Expensive and slightly risky, so it is asked
        /// for rather than assumed.
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
