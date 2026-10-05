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
        /// <summary>
        /// How many stacks one grant carries: what fits a relayed packet at the longest ids. A grant is not
        /// split, because its identity pays it once and a second part under the same one would be skipped as
        /// a repeat - but an exchange node hands over a few stacks, never ten.
        /// </summary>
        public const int MaxStacks = 10;

        /// <summary>The largest stack accepted. Quest handovers are single tools and small bundles.</summary>
        public const int MaxAmount = 999;

        /// <summary>The longest item id accepted.</summary>
        public const int MaxIdLength = 96;

        /// <summary>The longest identity accepted.</summary>
        public const int MaxIdentityLength = 96;

        /// <summary>
        /// Which exchange node granted it, as dialogue tree plus node id. What lets both machines
        /// pay each grant exactly once.
        /// </summary>
        public string Identity;

        /// <summary>The granted stacks.</summary>
        public LootStack[] Stacks;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(Identity, MaxIdentityLength));

            LootStack.WriteList(writer, Stacks, MaxStacks, MaxIdLength);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Identity = WireStrings.Cut(reader.GetString(), MaxIdentityLength);

            // Not clamped to MaxAmount. The receiver skips a stack above it rather than granting less.
            Stacks = LootStack.ReadList(reader, MaxStacks, MaxIdLength, int.MaxValue);
        }
    }
}
