using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One step of a trade between two players, in the game's own merchant window with the teammate's body as the trader.
    ///
    /// The one who opens asks (<see cref="StageAsk"/>) and gets the other's bag back (<see cref="StageBag"/>, in
    /// <see cref="Take"/>). What they then agree on in the window is proposed (<see cref="StageOffer"/>: what the
    /// opener gives and takes), and nothing leaves the other's bag until that player says yes (<see cref="StageYes"/>)
    /// and still holds what is taken - a no, or a bag that changed, undoes the opener's side (<see cref="StageNo"/>).
    ///
    /// Client to host, host on to <see cref="To"/>; the host stamps <see cref="From"/> from the connection.
    /// </summary>
    public struct TradeMessage : INetMessage
    {
        /// <summary>
        /// The most stacks one message carries per side. A relay forwards 1199 bytes; eight stacks of the longest ids each way
        /// stay under it. A bag goes in several <see cref="StageBag"/> messages and ends with <see cref="StageBagEnd"/>, an
        /// offer in several <see cref="StageOffer"/> messages ending with <see cref="StageOfferEnd"/> - there is no limit on items.
        /// </summary>
        public const int MaxStacks = 8;

        /// <summary>Opener to partner: open a trade, send me your bag.</summary>
        public const byte StageAsk = 0;

        /// <summary>Partner to opener: my bag, in <see cref="Take"/>.</summary>
        public const byte StageBag = 1;

        /// <summary>Opener to partner: I give <see cref="Give"/> and take <see cref="Take"/>. Yes or no?</summary>
        public const byte StageOffer = 2;

        /// <summary>Partner to opener: done on my side.</summary>
        public const byte StageYes = 3;

        /// <summary>Partner to opener: refused, or I no longer hold it; undo yours.</summary>
        public const byte StageNo = 4;

        /// <summary>Partner to opener: that was all of my bag.</summary>
        public const byte StageBagEnd = 5;

        /// <summary>Opener to partner: that was all of the offer; ask now. An offer goes in as many <see cref="StageOffer"/> pieces as it needs.</summary>
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

        /// <summary>What the opener takes from the partner; on <see cref="StageBag"/>, the partner's whole bag.</summary>
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
