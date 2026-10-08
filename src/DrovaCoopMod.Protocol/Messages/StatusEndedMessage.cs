using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A status effect ended on its owner's player, so every other machine removes it from that player's body.
    /// The host stamps <see cref="Player"/> from the connection.
    /// </summary>
    public struct StatusEndedMessage : INetMessage
    {
        /// <summary>The longest effect id carried: a guid.</summary>
        private const int MaxIdLength = 64;

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
