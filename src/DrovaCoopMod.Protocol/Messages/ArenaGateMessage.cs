using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A game arena gate barrier or fog patch changed on the machine running the fight. Owner to host, host to the rest.
    /// </summary>
    public struct ArenaGateMessage : INetMessage
    {
        private const int MaxPathLength = 96;

        /// <summary>The object, as "parent/name".</summary>
        public string Path;

        /// <summary>1 or 0 for a standing barrier, or a fog patch's <c>FogletFogBhvr.Mode</c>.</summary>
        public byte State;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(Path, MaxPathLength));
            writer.Put(State);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Path = WireStrings.Cut(reader.GetString(), MaxPathLength);
            State = reader.GetByte();
        }
    }
}
