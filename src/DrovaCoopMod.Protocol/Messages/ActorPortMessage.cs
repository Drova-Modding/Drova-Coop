using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A cutscene moved an NPC: teleported it, walked it somewhere, or sent it (or everybody) to its routine place.
    ///
    /// Story cutscenes run only on the machine of the player in the conversation, and some of them are
    /// what carries NPCs into the next chapter - the Nemeton party into the Red Tower. The host owns NPC
    /// placement, so a port that happened only on a client was pulled back by the host's answer, and a
    /// port on the host alone left the client's copies behind.
    /// </summary>
    public struct ActorPortMessage : INetMessage
    {
        /// <summary>The longest guid accepted.</summary>
        public const int MaxGuidLength = 64;

        /// <summary><see cref="Kind"/>: teleported to <see cref="X"/>, <see cref="Y"/>.</summary>
        public const byte KindTeleport = 0;

        /// <summary><see cref="Kind"/>: sent to its routine place - every actor when <see cref="Guid"/> is empty.</summary>
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
