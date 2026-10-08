using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a breakable cache spilled and what is left, as item keys and amounts. Spilled pickups mint
    /// random guids, so only the cache itself can be named.
    /// </summary>
    public struct CacheSpillMessage : INetMessage
    {
        /// <summary>The longest root key carried.</summary>
        private const int MaxKeyLength = 72;

        /// <summary>The longest item key carried. These are authored guids.</summary>
        private const int MaxItemKeyLength = 64;

        /// <summary>The most distinct items one cache crosses with, sized to fit a relayed packet.</summary>
        private const int MaxEntries = 15;

        /// <summary>The most of one item a cache may claim to hold.</summary>
        private const int MaxAmount = 999;

        /// <summary>
        /// What this cache rolled. The host keeps the first offer per cache, and its own outranks a client's.
        /// </summary>
        public const byte KindOffer = 0;

        /// <summary>
        /// What is left after somebody took from it. Always applied, never outranked.
        /// </summary>
        public const byte KindRemainder = 1;

        /// <summary>The cache's save root key, which both worlds agree on.</summary>
        public string RootKey;

        /// <summary>An offer or a remainder. Anything else reads as a remainder, which is never refused.</summary>
        public byte Kind;

        /// <summary>Who broke or emptied it. Set by the host from the connection.</summary>
        public byte Player;

        /// <summary>The item keys still lying there.</summary>
        public string[] Keys;

        /// <summary>How many of each, one per key.</summary>
        public int[] Amounts;

        /// <summary>
        /// Whether this names a cache a teammate could act on, with two arrays that agree.
        /// </summary>
        public readonly bool IsPlausible()
        {
            if (!ResourceWorkMessage.IsReplicableKey(RootKey)) return false;
            if (Keys == null || Amounts == null) return false;

            return Keys.Length == Amounts.Length && Keys.Length <= MaxEntries;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(RootKey, MaxKeyLength));
            writer.Put(Kind);
            writer.Put(Player);

            string[] keys = Keys ?? [];
            int[] amounts = Amounts ?? [];
            int count = keys.Length;

            if (count > amounts.Length) count = amounts.Length;
            if (count > MaxEntries) count = MaxEntries;

            writer.Put((byte)count);
            for (int index = 0; index < count; index++)
            {
                writer.Put(WireStrings.Cut(keys[index], MaxItemKeyLength));

                int amount = amounts[index];
                writer.Put(Math.Clamp(amount, 0, MaxAmount));
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            RootKey = reader.GetString(MaxKeyLength);

            byte kind = reader.GetByte();
            Kind = kind == KindOffer ? KindOffer : KindRemainder;

            Player = reader.GetByte();

            int count = reader.GetByte();
            if (count > MaxEntries) count = MaxEntries;

            Keys = new string[count];
            Amounts = new int[count];

            for (int index = 0; index < count; index++)
            {
                Keys[index] = reader.GetString(MaxItemKeyLength);

                int amount = reader.GetInt();
                Amounts[index] = Math.Clamp(amount, 0, MaxAmount);
            }
        }
    }
}
