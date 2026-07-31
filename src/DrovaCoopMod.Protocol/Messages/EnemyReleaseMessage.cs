using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The host giving a creature back: it is no longer the host's to decide, and the client should return
    /// it to being an ordinary local creature.
    ///
    /// The half that must never be lost. A bind that goes missing costs a creature whose health is not
    /// shared, which is where the mod started. A release that goes missing leaves a creature the client has
    /// made unkillable and nobody is going to kill - and since write-protection is not saved but the
    /// player's frustration is, the release path is also asserted every frame from local state rather than
    /// trusted to this message alone.
    /// </summary>
    public struct EnemyReleaseMessage : INetMessage
    {
        /// <summary>The creature has moved out of the range the host claims.</summary>
        public const byte ReasonOutOfRange = 0;

        /// <summary>The host no longer has a body for it - its chunk unloaded there.</summary>
        public const byte ReasonHostLostBody = 1;

        /// <summary>It died. The client will have been told separately; this closes the handle.</summary>
        public const byte ReasonDead = 2;

        /// <summary>The session is ending.</summary>
        public const byte ReasonTeardown = 3;

        /// <summary>Which creature.</summary>
        public EnemyNetId NetId;

        /// <summary>
        /// Why, for the log. Nothing depends on it - a release is a release - but a client that cannot say
        /// why a creature went back to being local is one nobody can diagnose.
        /// </summary>
        public byte Reason;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(NetId.Value);
            writer.Put(Reason);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());
            Reason = reader.GetByte();
        }
    }
}
