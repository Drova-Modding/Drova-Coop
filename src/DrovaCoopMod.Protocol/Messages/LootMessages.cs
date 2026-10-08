using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One stack of items, named by the item's readable id.
    /// </summary>
    public struct LootStack
    {
        /// <summary>The longest item id carried.</summary>
        public const int MaxIdLength = 64;

        /// <summary>The most of one item a stack may claim, clamped on read.</summary>
        public const int MaxAmount = 9999;

        /// <summary>The item's readable id.</summary>
        public string Id;

        /// <summary>How many.</summary>
        public int Amount;

        /// <summary>A byte count, then each stack as its id and amount. Amounts are clamped on read.</summary>
        internal static void WriteList(INetWriter writer, LootStack[]? stacks, int maxCount, int maxIdLength = MaxIdLength)
        {
            int count = stacks == null ? 0 : Math.Min(stacks.Length, maxCount);

            writer.Put((byte)count);
            for (int index = 0; index < count; index++)
            {
                writer.Put(WireStrings.Cut(stacks![index].Id, maxIdLength));
                writer.Put(stacks[index].Amount);
            }
        }

        /// <summary>Reads what <see cref="WriteList"/> wrote, holding each field to the given limits.</summary>
        internal static LootStack[] ReadList(INetReader reader, int maxCount, int maxIdLength = MaxIdLength, int maxAmount = MaxAmount)
        {
            int count = Math.Min(reader.GetByte(), maxCount);

            LootStack[] list = new LootStack[count];
            for (int index = 0; index < count; index++)
            {
                string id = WireStrings.Cut(reader.GetString(), maxIdLength);
                list[index] = new LootStack { Id = id, Amount = Math.Clamp(reader.GetInt(), 0, maxAmount) };
            }

            return list;
        }
    }

    /// <summary>
    /// Shared loot leaving a player's world: a pickup taken, or a container's remaining contents.
    /// Each game rolls its own chest contents, so a container is also offered unopened as <see cref="KindProposal"/>
    /// and the host keeps the first offer. Client to host, host to the rest.
    /// </summary>
    public struct LootTakenMessage : INetMessage
    {
        /// <summary>The longest root key carried.</summary>
        public const int MaxKeyLength = 72;

        /// <summary>The most stacks that fit a relayed packet. A fuller container crosses via <see cref="Split"/>.</summary>
        public const int MaxStacks = 15;

        /// <summary>A scene pickup, taken whole. <see cref="Remaining"/> is empty and the far copy is removed.</summary>
        public const byte KindPickup = 0;

        /// <summary>A container, whose far copy is set to <see cref="Remaining"/>.</summary>
        public const byte KindContainer = 1;
        /// <summary>A container's contents as rolled here, offered unopened. The host's own copy outranks a client's.</summary>
        public const byte KindProposal = 2;

        /// <summary>The save root key of a pickup or chest, or a <see cref="CreatureKey"/> for a body.</summary>
        public string RootKey;

        /// <summary>Pickup, container or proposal. Anything else reads as a container.</summary>
        public byte Kind;

        /// <summary>Who took it. Set by the host from the connection.</summary>
        public byte Player;

        /// <summary>What the container holds now. Empty for a pickup.</summary>
        public LootStack[] Remaining;

        /// <summary>More parts follow. The far copy is set, not added to, so nothing acts until the last part.</summary>
        public bool More;

        /// <summary>This message in relay-sized parts. Always at least one, since an emptied chest is news too.</summary>
        public readonly LootTakenMessage[] Split()
        {
            LootStack[] all = Remaining ?? [];
            LootTakenMessage[] parts = new LootTakenMessage[Math.Max(1, (all.Length + MaxStacks - 1) / MaxStacks)];

            for (int index = 0; index < parts.Length; index++)
            {
                parts[index] = this;
                parts[index].Remaining = [.. all.Skip(index * MaxStacks).Take(MaxStacks)];
                parts[index].More = index < parts.Length - 1;
            }

            return parts;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(RootKey, LootTakenMessage.MaxKeyLength));
            writer.Put(Kind);
            writer.Put(Player);
            writer.Put(More);
            LootStack.WriteList(writer, Remaining, MaxStacks);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            RootKey = WireStrings.Cut(reader.GetString(), LootTakenMessage.MaxKeyLength);

            byte kind = reader.GetByte();
            Kind = kind <= KindProposal ? kind : KindContainer;

            Player = reader.GetByte();
            More = reader.GetBool();
            Remaining = LootStack.ReadList(reader, MaxStacks);
        }

        /// <summary>Whether the key names a save root or creature a teammate could resolve.</summary>
        public readonly bool IsPlausible()
        {
            return ResourceWorkMessage.IsReplicableKey(RootKey) || IsCreatureKey(RootKey);
        }


        /// <summary>Marks a key as naming a creature. No save root key starts with it.</summary>
        private const char CreaturePrefix = 'E';

        /// <summary>
        /// The key a body's loot hangs off, by session handle, since a corpse's loot trigger has no save root.
        /// </summary>
        /// <param name="creature">The creature's session handle.</param>
        public static string CreatureKey(ushort creature)
        {
            return CreaturePrefix + creature.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Whether a key was made by <see cref="CreatureKey"/>.</summary>
        public static bool IsCreatureKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key[0] != CreaturePrefix) return false;
            if (key.Length < 2 || key.Length > 6) return false;

            for (int index = 1; index < key.Length; index++)
            {
                if (key[index] < '0' || key[index] > '9') return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Host to a joining client: every shared loot change this session has seen. Chunked, and
    /// <see cref="LootSnapshotEndMessage"/> closes it.
    /// </summary>
    public struct LootSnapshotMessage : INetMessage
    {
        /// <summary>One record per chunk, since a whole chest's stacks can fill a relayed packet.</summary>
        public const int MaxRecords = 1;

        /// <summary>One piece of loot as it stands.</summary>
        public struct Record
        {
            /// <summary>The save root key.</summary>
            public string RootKey;

            /// <summary>Pickup or container, as <see cref="LootTakenMessage"/> defines them.</summary>
            public byte Kind;

            /// <summary>What is left. Empty for a pickup.</summary>
            public LootStack[] Remaining;
        }

        /// <summary>The entries in this chunk.</summary>
        public Record[] Records;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            Record[] records = Records ?? [];
            int count = records.Length > MaxRecords ? MaxRecords : records.Length;

            writer.Put((byte)count);
            for (int index = 0; index < count; index++)
            {
                writer.Put(WireStrings.Cut(records[index].RootKey, LootTakenMessage.MaxKeyLength));
                writer.Put(records[index].Kind);
                LootStack.WriteList(writer, records[index].Remaining, LootTakenMessage.MaxStacks);
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            int count = reader.GetByte();
            if (count > MaxRecords) count = MaxRecords;

            Records = new Record[count];
            for (int index = 0; index < count; index++)
            {
                string key = WireStrings.Cut(reader.GetString(), LootTakenMessage.MaxKeyLength);
                byte kind = reader.GetByte();

                Records[index] = new Record
                {
                    RootKey = key,
                    Kind = kind == LootTakenMessage.KindPickup ? LootTakenMessage.KindPickup : LootTakenMessage.KindContainer,
                    Remaining = LootStack.ReadList(reader, LootTakenMessage.MaxStacks),
                };
            }
        }
    }

    /// <summary>Host to one client: the loot snapshot is complete.</summary>
    public struct LootSnapshotEndMessage : INetMessage
    {
        /// <summary>How many entries were sent.</summary>
        public ushort Count;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Count);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Count = reader.GetUShort();
        }
    }
}
