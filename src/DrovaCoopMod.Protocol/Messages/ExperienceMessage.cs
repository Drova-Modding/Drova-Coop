using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A kill's experience, paid in full to every player. Drova's level pacing assumes one player
    /// clearing the world, so splitting would leave both under-leveled.
    /// </summary>
    public struct ExperienceMessage : INetMessage
    {
        /// <summary>
        /// The most one creature can be worth. The amount is added straight to a player's total.
        /// </summary>
        public const int MaxAmount = 100_000;

        /// <summary>
        /// Who was paid. The host overwrites a client's claim with the id it assigned that peer.
        /// </summary>
        public PlayerId Earner;

        /// <summary>
        /// Which creature, by session handle. Mod-spawned creatures have a different guid on each machine.
        /// </summary>
        public EnemyNetId NetId;

        /// <summary>
        /// What it was worth on the awarding machine. Clamped on both sides.
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

            Amount = Clamp(reader.GetInt());
        }

        /// <summary>
        /// Whether this names a creature and pays something.
        /// </summary>
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
