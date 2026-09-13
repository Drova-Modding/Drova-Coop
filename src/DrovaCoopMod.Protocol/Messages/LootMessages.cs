using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One stack of items as a container holds it, named by the item's readable id so the far game
    /// can look it up in its own item database.
    /// </summary>
    public struct LootStack
    {
        /// <summary>The longest item id carried. Longer ones are cut, which makes them unresolvable, never harmful.</summary>
        public const int MaxIdLength = 64;

        /// <summary>The most of one item a stack may claim. Above this a stack is dropped on read.</summary>
        public const int MaxAmount = 9999;

        /// <summary>The item's readable id.</summary>
        public string Id;

        /// <summary>How many.</summary>
        public int Amount;

        internal static string Truncate(string value)
        {
            return value.Length <= MaxIdLength ? value : value[..MaxIdLength];
        }

        internal static void Write(INetWriter writer, LootStack stack)
        {
            writer.Put(Truncate(stack.Id ?? string.Empty));
            writer.Put(stack.Amount);
        }

        internal static LootStack Read(INetReader reader)
        {
            string id = Truncate(reader.GetString() ?? string.Empty);
            int amount = reader.GetInt();

            return new LootStack { Id = id, Amount = amount < 0 ? 0 : (amount > MaxAmount ? MaxAmount : amount) };
        }

        internal static void WriteList(INetWriter writer, LootStack[]? stacks, int max)
        {
            LootStack[] list = stacks ?? [];
            int count = list.Length > max ? max : list.Length;

            writer.Put((byte)count);
            for (int index = 0; index < count; index++)
            {
                Write(writer, list[index]);
            }
        }

        internal static LootStack[] ReadList(INetReader reader, int max)
        {
            int count = reader.GetByte();
            if (count > max) count = max;

            LootStack[] list = new LootStack[count];
            for (int index = 0; index < count; index++)
            {
                list[index] = Read(reader);
            }

            return list;
        }
    }

    /// <summary>
    /// One shared piece of loot leaving a player's world: a pickup taken, or a container's contents
    /// after a player took from it. Under the shared loot rule the same thing then leaves every world.
    ///
    /// A container crosses as what is left in it rather than as what was taken, because the two
    /// worlds never rolled the same contents to begin with: each game fills a chest from its loot table
    /// at spawn. Carrying the remainder makes the first player to open a chest the one who decides
    /// what it held, and the far copy becomes that remainder whatever it rolled itself.
    ///
    /// Client to host, host to everyone else. The host stamps <see cref="Player"/> from the connection.
    /// </summary>
    public struct LootTakenMessage : INetMessage
    {
        /// <summary>The longest root key carried. A save root key is a guid or two, never longer.</summary>
        public const int MaxKeyLength = 72;

        /// <summary>The most stacks one container crosses with.</summary>
        public const int MaxStacks = 64;

        /// <summary>A scene pickup, taken whole. <see cref="Remaining"/> is empty and the far copy is removed.</summary>
        public const byte KindPickup = 0;

        /// <summary>A container, whose far copy is set to <see cref="Remaining"/>.</summary>
        public const byte KindContainer = 1;

        /// <summary>The save root key of the pickup or container, which both worlds agree on.</summary>
        public string RootKey;

        /// <summary>Pickup or container. Anything else reads as a container.</summary>
        public byte Kind;

        /// <summary>Who took it. Set by the host from the connection, not trusted from the payload.</summary>
        public byte Player;

        /// <summary>What the container holds now. Empty for a pickup.</summary>
        public LootStack[] Remaining;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(TruncateKey(RootKey ?? string.Empty));
            writer.Put(Kind);
            writer.Put(Player);
            LootStack.WriteList(writer, Remaining, MaxStacks);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            RootKey = TruncateKey(reader.GetString() ?? string.Empty);

            byte kind = reader.GetByte();
            Kind = kind == KindPickup ? KindPickup : KindContainer;

            Player = reader.GetByte();
            Remaining = LootStack.ReadList(reader, MaxStacks);
        }

        /// <summary>
        /// Whether the key names something a teammate could resolve. The same rule as resource spots
        /// and for the same reason: a dynamic root's number means something different in every save.
        /// </summary>
        public readonly bool IsPlausible()
        {
            return ResourceWorkMessage.IsReplicableKey(RootKey);
        }

        internal static string TruncateKey(string value)
        {
            return value.Length <= MaxKeyLength ? value : value[..MaxKeyLength];
        }
    }

    /// <summary>
    /// Host to one client on joining: every shared piece of loot this session has already seen go, so
    /// a late joiner's world catches up. Chunked; <see cref="LootSnapshotEndMessage"/> closes it.
    /// </summary>
    public struct LootSnapshotMessage : INetMessage
    {
        /// <summary>The most entries in one chunk.</summary>
        public const int MaxRecords = 8;

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
                writer.Put(LootTakenMessage.TruncateKey(records[index].RootKey ?? string.Empty));
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
                string key = LootTakenMessage.TruncateKey(reader.GetString() ?? string.Empty);
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

    /// <summary>
    /// Host to one client: the loot snapshot is complete, and this is how many entries it held.
    /// </summary>
    public struct LootSnapshotEndMessage : INetMessage
    {
        /// <summary>How many entries were sent, so the receiver can say whether all arrived.</summary>
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
