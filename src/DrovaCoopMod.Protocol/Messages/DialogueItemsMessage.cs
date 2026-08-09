using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Items a dialogue granted one player, given to everybody.
    ///
    /// A dialogue's exchange node fills the hands of the player standing in the conversation - the
    /// teammate on the same quest walks the same mine without the pickaxe the story assumes they
    /// hold. Grants carry the same node identity dialogue experience travels under, so each node
    /// pays every player exactly once however many of them sit through the conversation. Nodes
    /// that also take something from the player are trades and never sent - the teammate did not
    /// pay.
    /// </summary>
    public struct DialogueItemsMessage : INetMessage
    {
        /// <summary>How many stacks one grant carries.</summary>
        public const int MaxStacks = 16;

        /// <summary>The largest stack accepted. Quest handovers are single tools and small bundles.</summary>
        public const int MaxAmount = 999;

        /// <summary>The longest item id accepted.</summary>
        public const int MaxIdLength = 96;

        /// <summary>The longest identity accepted.</summary>
        public const int MaxIdentityLength = 96;

        /// <summary>One granted stack.</summary>
        public struct Stack
        {
            /// <summary>Which item, by its readable id - the name equipment already travels under.</summary>
            public string Id;

            /// <summary>How many.</summary>
            public int Amount;
        }

        /// <summary>
        /// Which exchange node granted it, as dialogue tree plus node id. What lets both machines
        /// pay each grant exactly once.
        /// </summary>
        public string Identity;

        /// <summary>The granted stacks.</summary>
        public Stack[] Stacks;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Truncate(Identity ?? string.Empty, MaxIdentityLength));

            int count = Stacks == null ? 0 : System.Math.Min(Stacks.Length, MaxStacks);

            writer.Put((byte)count);

            for (int index = 0; index < count; index++)
            {
                writer.Put(Truncate(Stacks![index].Id ?? string.Empty, MaxIdLength));
                writer.Put(Stacks[index].Amount);
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Identity = Truncate(reader.GetString() ?? string.Empty, MaxIdentityLength);

            int count = System.Math.Min((int)reader.GetByte(), MaxStacks);

            Stacks = new Stack[count];

            for (int index = 0; index < count; index++)
            {
                Stacks[index] = new Stack
                {
                    Id = Truncate(reader.GetString() ?? string.Empty, MaxIdLength),
                    Amount = reader.GetInt(),
                };
            }
        }

        private static string Truncate(string value, int limit)
        {
            return value.Length <= limit ? value : value[..limit];
        }
    }
}
