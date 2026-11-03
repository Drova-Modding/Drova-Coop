using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The creature a player is morphed into, so their body elsewhere becomes that creature too.
    /// </summary>
    public record struct MorphMessage : INetMessage
    {
        /// <summary>The longest guid kept, cut on write and on read.</summary>
        public const int MaxGuidLength = 64;

        /// <summary>Who morphed. Stamped by the session.</summary>
        public PlayerId PlayerId;

        /// <summary>Addressable guid of the creature prefab. Empty when the morph ended.</summary>
        public string CreatureGuid;

        /// <summary>The creature's <c>EquipIndex</c> weapon set, so its body elsewhere replays the same attacks. -1 for its first.</summary>
        public sbyte WeaponSet;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(PlayerId.Value);
            writer.Put(WireStrings.Cut(CreatureGuid ?? string.Empty, MaxGuidLength));
            writer.Put(WeaponSet);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            PlayerId = new PlayerId(reader.GetByte());
            CreatureGuid = WireStrings.Cut(reader.GetString(), MaxGuidLength);
            WeaponSet = reader.GetSByte();
        }
    }
}
