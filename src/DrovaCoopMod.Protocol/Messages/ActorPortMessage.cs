using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A cutscene moved an NPC. Cutscenes run on one machine only, so the move is relayed to the rest.
    /// </summary>
    public struct ActorPortMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        private const int MaxGuidLength = 64;

        /// <summary><see cref="Kind"/>: teleported to <see cref="X"/>, <see cref="Y"/>.</summary>
        public const byte KindTeleport = 0;

        /// <summary><see cref="Kind"/>: sent to its routine place, or every actor when <see cref="Guid"/> is empty.</summary>
        public const byte KindRoutine = 1;

        /// <summary><see cref="Kind"/>: walked toward <see cref="X"/>, <see cref="Y"/>.</summary>
        public const byte KindWalk = 2;

        /// <summary><see cref="Kind"/>: run toward <see cref="X"/>, <see cref="Y"/>.</summary>
        public const byte KindRun = 3;

        /// <summary>What happened.</summary>
        public byte Kind;

        /// <summary>The actor's lazy-actor guid, or empty for every actor.</summary>
        public string Guid;

        /// <summary>Where a teleport put it, or where a walk heads.</summary>
        public float X;

        /// <inheritdoc cref="X"/>
        public float Y;

        /// <summary>Whether the teleport also moved its routine origin there.</summary>
        public bool ResetRoutine;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Kind);
            writer.Put(WireStrings.Cut(Guid, MaxGuidLength));
            writer.Put(X);
            writer.Put(Y);
            writer.Put(ResetRoutine);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Kind = reader.GetByte();
            Guid = WireStrings.Cut(reader.GetString(), MaxGuidLength);
            X = reader.GetFloat();
            Y = reader.GetFloat();
            ResetRoutine = reader.GetBool();
        }
    }
}
