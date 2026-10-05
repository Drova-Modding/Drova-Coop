using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One step of a co-op checkpoint: a save every machine writes at the same moment, or nobody does.
    ///
    /// The host asks (<see cref="StageAsk"/>), every teammate answers whether the game would let it save right now,
    /// and only when all can does the host say <see cref="StageCommit"/>. A client that saved on its own sends an ask
    /// to the host, which is how it asks for a round.
    /// </summary>
    public struct CheckpointMessage : INetMessage
    {
        /// <summary>Host to all: can you save now? Client to host: I saved, start a round.</summary>
        public const byte StageAsk = 0;

        /// <summary>Client to host: yes.</summary>
        public const byte StageReady = 1;

        /// <summary>Client to host: no - in a fight, a dialogue, a scene change.</summary>
        public const byte StageBusy = 2;

        /// <summary>Host to all: everyone can; write the checkpoint now.</summary>
        public const byte StageCommit = 3;

        /// <summary>Which round, counted by the host.</summary>
        public ushort Id;

        /// <summary>One of the Stage constants.</summary>
        public byte Stage;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Id);
            writer.Put(Stage);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Id = reader.GetUShort();
            Stage = reader.GetByte();
        }
    }
}
