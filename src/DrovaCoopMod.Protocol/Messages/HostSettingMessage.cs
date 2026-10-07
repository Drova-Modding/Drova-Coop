using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to clients: the host's value of one setting a mod shared with <c>CoopAccess.ShareHostSetting</c>.
    /// Sent to a joiner for every setting, and to everyone when one changes.
    /// </summary>
    public struct HostSettingMessage : INetMessage
    {
        /// <summary>The longest key accepted.</summary>
        public const int MaxKeyLength = 64;

        /// <summary>The longest value accepted.</summary>
        public const int MaxValueLength = 256;

        /// <summary>The setting, as the mod registered it.</summary>
        public string Key;

        /// <summary>The host's value.</summary>
        public string Value;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(Key, MaxKeyLength));
            writer.Put(WireStrings.Cut(Value, MaxValueLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Key = WireStrings.Cut(reader.GetString(), MaxKeyLength);
            Value = WireStrings.Cut(reader.GetString(), MaxValueLength);
        }
    }
}
