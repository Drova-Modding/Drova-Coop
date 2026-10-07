using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A game arena gate barrier or fog patch changed on the machine running the fight. Owner to host, host to the rest.
    /// </summary>
    public struct ArenaGateMessage : INetMessage
    {
        /// <summary>The longest path carried: parent and object name.</summary>
        public const int MaxPathLength = 96;

        /// <summary>The object, as "parent/name".</summary>
        public string Path;

        /// <summary>A barrier: 1 standing, 0 not. A fog patch: its mode (FogletFogBhvr.Mode).</summary>
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
