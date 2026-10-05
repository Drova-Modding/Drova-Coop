using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A piece of the game arena's scene changed in the machine whose arena runs the fight - a gate barrier switched, or
    /// Ada's fog changing mode; every other machine sets its own to match. Owner to host, host to everyone else.
    /// </summary>
    public struct ArenaGateMessage : INetMessage
    {
        /// <summary>The longest path carried: parent and object name.</summary>
        public const int MaxPathLength = 96;

        /// <summary>The object, as "parent/name" - the scene's own names, the same on every machine.</summary>
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
