using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a creature was worth, so everybody is paid for it.
    ///
    /// Experience is the one reward two players cannot both find in the world. Loot lies on the ground
    /// and both can walk to it; a level is a number in one player's save and nothing in a shared world puts
    /// it in the other's. Left alone, whoever landed the killing blow levels and the other does not, and
    /// after a few hours of that the two are playing different games - the same camp is a fight for one of
    /// them and a massacre for the other, which is the point at which co-op stops working at all.
    ///
    /// The full amount to everybody, not a share. Splitting is the obvious reading of "shared" and it is
    /// the wrong one here: Drova's levels are paced against one player clearing the world once, so halving
    /// every kill leaves both players permanently under-levelled for the content in front of them. Two
    /// players killing twice as much between them is what keeps the pacing intact.
    ///
    /// Everyone, both directions, like <see cref="KnowledgeMessage"/> and for the same reason: there is
    /// no attacker and no victim here, only a machine that awarded something and machines that have not.
    ///
    /// Sent reliably and once. The receiving side keys on <see cref="NetId"/> and refuses a creature it has
    /// already been paid for, so a repeat is harmless - but a lost one is experience nobody ever gets, and
    /// nothing restates it.
    /// </summary>
    public struct ExperienceMessage : INetMessage
    {
        /// <summary>
        /// The most one creature can be worth. Generous next to the game's own figures, which are tens; what
        /// it is for is that the amount is added straight to a player's total, so an unchecked one is a
        /// single packet that ends somebody's progression.
        /// </summary>
        public const int MaxAmount = 100_000;

        /// <summary>
        /// Who was paid, and therefore who is telling everybody. The host overwrites a client's claim with
        /// the id it assigned that peer.
        /// </summary>
        public PlayerId Earner;

        /// <summary>
        /// Which creature, by the session's own handle for it.
        ///
        /// Not its guid. A creature another mod spawned has a different guid on each machine and the same
        /// handle, so a guid here would silently stop paying for exactly the creatures a shared world had to
        /// work hardest to produce.
        /// </summary>
        public EnemyNetId NetId;

        /// <summary>
        /// What it was worth, as the awarding machine's own game calculated it. Clamped on both sides.
        /// </summary>
        public int Amount;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Earner.Value);
            writer.Put(NetId.Value);
            writer.Put(Clamp(Amount));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Earner = new PlayerId(reader.GetByte());
            NetId = new EnemyNetId(reader.GetUShort());

            // Clamped on read as well as on write: the sender is not the one to trust about how much its
            // own kill was worth.
            Amount = Clamp(reader.GetInt());
        }

        /// <summary>
        /// Whether this is worth acting on at all.
        /// </summary>
        /// <returns>False for a handle that names nothing, or an amount that would pay nothing.</returns>
        public readonly bool IsPlausible()
        {
            return NetId != EnemyNetId.None && Amount > 0;
        }

        private static int Clamp(int value)
        {
            if (value <= 0) return 0;

            return value > MaxAmount ? MaxAmount : value;
        }
    }
}
