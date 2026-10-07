using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A non-player actor died, so it stays dead for everybody. Sent to everyone, reliable and idempotent.
    /// Keyed by the scene-authored <c>GuidComponent</c> guid, which the game's own saves also key on.
    /// </summary>
    public struct EnemyDeathMessage : INetMessage
    {
        /// <summary>The longest guid accepted, exactly the 36 characters <c>GuidComponent.CreateGuid</c> checks for.</summary>
        public const int MaxGuidLength = WireStrings.MaxGuidLength;

        /// <summary>
        /// A death restored from a save rather than witnessed. Applied with <c>HealthChangeArgs.SetIsLoadingArg</c>
        /// so no death animation, ragdoll, experience or death cast replays.
        /// </summary>
        public const byte FlagReconcile = 1 << 0;

        /// <summary>Which actor died, as its <c>GuidComponent</c> guid. Never null after a read.</summary>
        public string ActorGuid;

        /// <summary>See <see cref="FlagReconcile"/>. Unknown bits are ignored so a later version can add one.</summary>
        public byte Flags;

        /// <summary>Whether this death is a save being reconciled rather than a kill.</summary>
        public readonly bool IsReconcile => (Flags & FlagReconcile) != 0;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(ActorGuid, MaxGuidLength));
            writer.Put(Flags);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            // Truncate on read too, the sender is not trusted.
            ActorGuid = WireStrings.Cut(reader.GetString(), MaxGuidLength);
            Flags = reader.GetByte();
        }
    }
}
