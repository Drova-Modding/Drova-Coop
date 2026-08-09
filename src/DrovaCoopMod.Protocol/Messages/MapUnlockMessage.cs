using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A map one player unlocked, so everybody can read it.
    ///
    /// Maps are handed out by dialogue tasks, which run for the player standing in the
    /// conversation. The unlock crosses by the map definition's authored guid and is applied
    /// through the game's own enable call, which is change-guarded and therefore repeat-safe.
    /// </summary>
    public struct MapUnlockMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = 36;

        /// <summary>The map definition's authored guid.</summary>
        public string Guid;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Truncate(Guid ?? string.Empty));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Guid = Truncate(reader.GetString() ?? string.Empty);
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxGuidLength ? value : value[..MaxGuidLength];
        }
    }
}
