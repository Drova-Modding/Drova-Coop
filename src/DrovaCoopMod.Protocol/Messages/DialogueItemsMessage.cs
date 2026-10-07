using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Items a dialogue exchange node granted one player, given to everybody once per node identity.
    /// Nodes that also take something are trades and never sent.
    /// </summary>
    public struct DialogueItemsMessage : INetMessage
    {
        /// <summary>Stacks per grant. A grant is never split, since a second part under the same identity would be skipped.</summary>
        public const int MaxStacks = 10;

        /// <summary>The largest stack accepted.</summary>
        public const int MaxAmount = 999;

        /// <summary>The longest item id accepted.</summary>
        public const int MaxIdLength = 96;

        /// <summary>The longest identity accepted.</summary>
        public const int MaxIdentityLength = 96;

        /// <summary>Dialogue tree plus node id, so each grant is paid exactly once.</summary>
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
