using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Host to everyone: the full set of session rules, sent reliably when one changes.
    /// The handshake carries the same set for a joiner.
    /// </summary>
    public struct SessionRulesMessage : INetMessage
    {
        /// <summary>Whether players may damage each other. Only the host's answer counts.</summary>
        public bool FriendlyFire;

        /// <summary>Health a downed player stands up with, as a percentage of their maximum. Zero means unset.</summary>
        public byte RecoveryHealthPercent;

        /// <summary>Creature toughness as a percentage of authored health. Zero means unset.</summary>
        public ushort EnemyHealthPercent;

        /// <summary>
        /// The host's loot rule, as <see cref="CoopProtocol.LootModeInstanced"/> or
        /// <see cref="CoopProtocol.LootModeShared"/>. Anything else reads as instanced.
        /// </summary>
        public byte LootMode;

        /// <summary>The host's savegame mode (<c>Il2CppDrova.GameMode</c>), or <see cref="UnknownGameMode"/> with no save loaded.</summary>
        public byte GameMode;

        /// <summary>No savegame loaded, so no mode to match.</summary>
        public const byte UnknownGameMode = byte.MaxValue;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(FriendlyFire);
            writer.Put(RecoveryHealthPercent);
            writer.Put(EnemyHealthPercent);
            writer.Put(LootMode);
            writer.Put(GameMode);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            FriendlyFire = reader.GetBool();
            RecoveryHealthPercent = reader.GetByte();
            EnemyHealthPercent = reader.GetUShort();

            byte lootMode = reader.GetByte();
            LootMode = lootMode == CoopProtocol.LootModeShared ? CoopProtocol.LootModeShared : CoopProtocol.LootModeInstanced;
            GameMode = reader.GetByte();
        }
    }
}
