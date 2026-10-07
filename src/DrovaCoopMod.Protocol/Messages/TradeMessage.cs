using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One step of a player-to-player trade in the merchant window. Nothing leaves the partner's bag until they say yes.
    /// </summary>
    public struct TradeMessage : INetMessage
    {
        /// <summary>Most stacks per side in one message, to stay under the 1199-byte relay limit. Bags and offers span messages.</summary>
        public const int MaxStacks = 8;

        /// <summary>Opener to partner: open a trade, send me your bag.</summary>
        public const byte StageAsk = 0;

        /// <summary>Partner to opener: my bag, in <see cref="Take"/>.</summary>
        public const byte StageBag = 1;

        /// <summary>Opener to partner: I give <see cref="Give"/> and take <see cref="Take"/>. Yes or no?</summary>
        public const byte StageOffer = 2;

        /// <summary>Partner to opener: done on my side.</summary>
        public const byte StageYes = 3;

        /// <summary>Partner to opener: refused, or I no longer hold it. Undo yours.</summary>
        public const byte StageNo = 4;

        /// <summary>Partner to opener: that was all of my bag.</summary>
        public const byte StageBagEnd = 5;

        /// <summary>Opener to partner: that was all of the offer, ask now.</summary>
        public const byte StageOfferEnd = 6;

        /// <summary>Which trade, counted by the opener.</summary>
        public ushort Id;

        /// <summary>One of the Stage constants.</summary>
        public byte Stage;

        /// <summary>Who sent it. Stamped by the host.</summary>
        public PlayerId From;

        /// <summary>Who it is for.</summary>
        public PlayerId To;

        /// <summary>What the opener gives the partner.</summary>
        public LootStack[] Give;

        /// <summary>What the opener takes from the partner. On <see cref="StageBag"/>, the partner's whole bag.</summary>
        public LootStack[] Take;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Id);
            writer.Put(Stage);
            writer.Put(From.Value);
            writer.Put(To.Value);
            LootStack.WriteList(writer, Give, MaxStacks);
            LootStack.WriteList(writer, Take, MaxStacks);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Id = reader.GetUShort();
            Stage = reader.GetByte();
            From = new PlayerId(reader.GetByte());
            To = new PlayerId(reader.GetByte());
            Give = LootStack.ReadList(reader, MaxStacks);
            Take = LootStack.ReadList(reader, MaxStacks);
        }
    }
}
