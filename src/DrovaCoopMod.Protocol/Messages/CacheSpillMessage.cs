using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a breakable cache spilled, and what is left of it.
    ///
    /// A cache scatters loose pickups on the ground rather than opening a window, and the pickups
    /// themselves cannot be named: their prefab ships with no authored guid, so each one mints a fresh
    /// random guid when it wakes. Two machines breaking the same crate produce different objects with
    /// different names, different counts and different items, because the roll is unseeded.
    ///
    /// So nothing here names a pickup. The cache is named - it is a scene object with a real save root -
    /// and what crosses is the pair of arrays the game itself reduces a cache to when it saves one: the
    /// item keys and how many of each are still lying there. Pickups of one key are interchangeable, so
    /// "this cache still holds two of these" says everything anybody needs.
    ///
    /// Client to host, host to everyone else. The host stamps <see cref="Player"/> from the connection.
    /// </summary>
    public struct CacheSpillMessage : INetMessage
    {
        /// <summary>The longest root key carried.</summary>
        public const int MaxKeyLength = 72;

        /// <summary>The longest item key carried. These are authored guids.</summary>
        public const int MaxItemKeyLength = 64;

        /// <summary>
        /// The most distinct items one cache crosses with: what fits a relayed packet at the longest keys.
        /// A crate spills a handful of things, so this is a bound rather than a limit anybody meets.
        /// </summary>
        public const int MaxEntries = 15;

        /// <summary>The most of one item a cache may claim to hold.</summary>
        public const int MaxAmount = 999;

        /// <summary>
        /// What this cache rolled, offered by the machine that broke it first. The host keeps the first
        /// offer per cache and its own outranks a client's, because the host's save is the world.
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
            RootKey = reader.GetString(MaxKeyLength) ?? string.Empty;

            byte kind = reader.GetByte();
            Kind = kind == KindOffer ? KindOffer : KindRemainder;

            Player = reader.GetByte();

            int count = reader.GetByte();
            if (count > MaxEntries) count = MaxEntries;

            Keys = new string[count];
            Amounts = new int[count];

            for (int index = 0; index < count; index++)
            {
                Keys[index] = reader.GetString(MaxItemKeyLength) ?? string.Empty;

                int amount = reader.GetInt();
                Amounts[index] = Math.Clamp(amount, 0, MaxAmount);
            }
        }
    }
}
