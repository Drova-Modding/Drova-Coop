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


        internal static void Write(INetWriter writer, LootStack stack)
        {
            writer.Put(WireStrings.Cut(stack.Id, MaxIdLength));
            writer.Put(stack.Amount);
        }

        internal static LootStack Read(INetReader reader)
        {
            string id = WireStrings.Cut(reader.GetString(), MaxIdLength);
            int amount = reader.GetInt();

            return new LootStack { Id = id, Amount = Math.Clamp(amount, 0, MaxAmount) };
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
    /// at spawn. So a container is also offered before anyone opens it, as <see cref="KindProposal"/>:
    /// the machine that streams a chest in tells the session what it rolled, the host keeps the first
    /// offer per chest, and every other copy becomes that. Without the offer two players opening the
    /// same chest at once would each take from their own roll and both takes would stand.
    ///
    /// Client to host, host to everyone else. The host stamps <see cref="Player"/> from the connection.
    /// A refused offer is answered to its sender alone with the entry that stands.
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
        /// <summary>
        /// A container's contents as rolled here, offered before anyone opened it. Applied like a
        /// container where it is accepted; the host keeps the first offer and its own copy outranks a
        /// client's, since the host's save is the world.
        /// </summary>
        public const byte KindProposal = 2;

        /// <summary>
        /// What the loot hangs off, which both worlds agree on: the save root key of a pickup or chest,
        /// or a creature handle for a body.
        /// </summary>
        public string RootKey;

        /// <summary>Pickup, container or proposal. Anything else reads as a container.</summary>
        public byte Kind;

        /// <summary>Who took it. Set by the host from the connection, not trusted from the payload.</summary>
        public byte Player;

        /// <summary>What the container holds now. Empty for a pickup.</summary>
        public LootStack[] Remaining;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(RootKey, LootTakenMessage.MaxKeyLength));
            writer.Put(Kind);
            writer.Put(Player);
            LootStack.WriteList(writer, Remaining, MaxStacks);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            RootKey = WireStrings.Cut(reader.GetString(), LootTakenMessage.MaxKeyLength);

            byte kind = reader.GetByte();
            Kind = kind <= KindProposal ? kind : KindContainer;

            Player = reader.GetByte();
            Remaining = LootStack.ReadList(reader, MaxStacks);
        }

        /// <summary>
        /// Whether the key names something a teammate could resolve: a save root both saves agree on,
        /// or a creature both machines hold the same handle for.
        /// </summary>
        public readonly bool IsPlausible()
        {
            return ResourceWorkMessage.IsReplicableKey(RootKey) || IsCreatureKey(RootKey);
        }


        /// <summary>
        /// Marks a key as naming a creature rather than a save root. No save root key starts with it:
        /// they are hexadecimal guids or the resource spots' own position form.
        /// </summary>
        public const char CreaturePrefix = 'E';

        /// <summary>
        /// The key a body's loot hangs off. A corpse has no save root of its own - the game spawns its
        /// loot trigger at runtime and parents it to whatever died - so the creature is the only thing
        /// both machines can name it by, and the session handle is the name both already agree on. A
        /// guid would not do: a creature another mod spawned has a different one at each end.
        /// </summary>
        /// <param name="creature">The creature's session handle.</param>
        public static string CreatureKey(ushort creature)
        {
            return CreaturePrefix + creature.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Whether a key was made by <see cref="CreatureKey"/>. A bound rather than a parse, because the
        /// key is only ever compared as text.
        /// </summary>
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
