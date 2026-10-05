using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A foglet fog patch woke on one machine: its foglets are out. Every other machine wakes its copy too, so the party
    /// meets one set at one moment instead of each player's patch rolling its own. Client to host, host to everyone else.
    /// </summary>
    public struct FogletWakeMessage : INetMessage
    {
        /// <summary>The longest key carried: a scene guid.</summary>
        public const int MaxKeyLength = 64;

        /// <summary>The patch, named by its first foglet's scene guid - the same on every machine.</summary>
        public string Key;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(Key, MaxKeyLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Key = WireStrings.Cut(reader.GetString(), MaxKeyLength);
        }
    }
}
