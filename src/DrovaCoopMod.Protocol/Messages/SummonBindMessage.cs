using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Pairs a conjured creature with the host's handle, keyed by caster, cast number and spawn order.
    /// Both machines build their own summons from the replicated cast, so an unmatched bind is held until the body exists.
    /// </summary>
    public struct SummonBindMessage : INetMessage
    {
        /// <summary>Highest summon index accepted per cast. Bounds what a peer can make the far side look up.</summary>
        public const byte MaxIndex = 32;

        /// <summary>Whose spell it was. The host overwrites a client's claim with the id it assigned.</summary>
        public PlayerId Caster;

        /// <summary>Which of that player's casts, as the cast replication numbers them. Wraps.</summary>
        public byte CastSequence;

        /// <summary>Which summon of that cast, in the order the conjure spawns them.</summary>
        public byte Index;

        /// <summary>The handle the host gave its own copy.</summary>
        public EnemyNetId NetId;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Caster.Value);
            writer.Put(CastSequence);
            writer.Put(Index);
            writer.Put(NetId.Value);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Caster = new PlayerId(reader.GetByte());
            CastSequence = reader.GetByte();
            Index = reader.GetByte();
            NetId = new EnemyNetId(reader.GetUShort());
        }

        /// <summary>Whether this names a summon that could exist.</summary>
        public readonly bool IsPlausible()
        {
            return NetId != EnemyNetId.None && Index < MaxIndex;
        }
    }
}
