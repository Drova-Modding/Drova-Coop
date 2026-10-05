using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A status effect ended on its owner's own player - cured, cleansed, or run out. Every other machine takes it off
    /// that player's body, so a cured poison does not stay on the copies (and an effect that ticks there does not go on
    /// ticking). Owner to host, host to everyone else; the host stamps <see cref="Player"/> from the connection.
    /// </summary>
    public struct StatusEndedMessage : INetMessage
    {
        /// <summary>The longest effect id carried: a guid.</summary>
        public const int MaxIdLength = 64;

        /// <summary>Whose effect it was.</summary>
        public PlayerId Player;

        /// <summary>The effect's prefab guid.</summary>
        public string EffectId;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Player.Value);
            writer.Put(WireStrings.Cut(EffectId, MaxIdLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Player = new PlayerId(reader.GetByte());
            EffectId = WireStrings.Cut(reader.GetString(), MaxIdLength);
        }
    }
}
