using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Which handle one conjured creature answers to, named by the cast that produced it.
    ///
    /// Both machines already have the creature; what they lack is a shared name for it. A spell that
    /// conjures is replicated as a cast - the far side's copy of the caster plays the same ability from the
    /// same prefab - so each machine builds its own summons without being told to. They are the same
    /// creatures in every respect the player can see, and in none that the session can use: each was
    /// instantiated locally, so each carries a guid minted in its own process.
    ///
    /// So the pairing rides on the one thing both machines agree about, which is the cast. A conjure spawns
    /// its creatures in a fixed order from a fixed list, so the *n*th summon of a given player's *s*th cast
    /// is the same creature everywhere. The host claims its copy, says which handle it gave it, and the far
    /// side attaches that handle to the body it already built.
    ///
    /// After this arrives the summon is an ordinary member of the session: its health, its position and its
    /// death all travel under the handle. Before it arrives the two copies fight independently, which is
    /// what they did before any of this existed.
    ///
    /// Sent reliably and once per summon. The far side may not have the body yet - a conjure can be delayed
    /// - so an unmatched one is held rather than dropped.
    /// </summary>
    public struct SummonBindMessage : INetMessage
    {
        /// <summary>
        /// The most summons one cast is paired for.
        ///
        /// The game's own conjures make a handful. This is the point past which an index is not a summon
        /// this build understands, and it bounds what a peer can make the far side look up.
        /// </summary>
        public const byte MaxIndex = 32;

        /// <summary>Whose spell it was. The host overwrites a client's claim with the id it assigned.</summary>
        public PlayerId Caster;

        /// <summary>
        /// Which of that player's casts, as the cast replication already numbers them. Wraps, like every
        /// other sequence here; a stale one names a cast whose summons are long gone and matches nothing.
        /// </summary>
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

        /// <summary>
        /// Whether this names a summon that could exist.
        /// </summary>
        /// <returns>False for a handle that names nothing or an index past what a conjure produces.</returns>
        public readonly bool IsPlausible()
        {
            return NetId != EnemyNetId.None && Index < MaxIndex;
        }
    }
}
