using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One step of a co-op checkpoint: every machine saves at the same moment, or nobody does.
    /// The host commits only once every teammate answered ready.
    /// </summary>
    public struct CheckpointMessage : INetMessage
    {
        /// <summary>Host to all: can you save now? Client to host: I saved, start a round.</summary>
        public const byte StageAsk = 0;

        /// <summary>Client to host: yes.</summary>
        public const byte StageReady = 1;

        /// <summary>Client to host: no, the game cannot save now.</summary>
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
