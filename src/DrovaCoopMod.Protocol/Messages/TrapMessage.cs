using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A trap a creature laid in the machine running its fight (an arena champion's), or that trap gone - fired, disarmed
    /// or expired. Every other machine shows the same trap at the same spot, inert, and takes it away with the original.
    /// Owner to host, host to everyone else.
    /// </summary>
    public struct TrapMessage : INetMessage
    {
        /// <summary>The longest prefab name carried.</summary>
        public const int MaxPrefabLength = 64;

        /// <summary>Laid: <see cref="Prefab"/> stands at the spot now.</summary>
        public const byte OpPlaced = 0;

        /// <summary>Gone: the trap at the spot is no more.</summary>
        public const byte OpGone = 1;

        /// <summary><see cref="OpPlaced"/> or <see cref="OpGone"/>.</summary>
        public byte Op;

        /// <summary>The trap's prefab, by name; empty for <see cref="OpGone"/>.</summary>
        public string Prefab;

        /// <summary>Where it lies.</summary>
        public float X;

        /// <summary>See <see cref="X"/>.</summary>
        public float Y;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Op);
            writer.Put(WireStrings.Cut(Prefab, MaxPrefabLength));
            writer.Put(X);
            writer.Put(Y);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Op = reader.GetByte();
            Prefab = WireStrings.Cut(reader.GetString(), MaxPrefabLength);
            X = reader.GetFloat();
            Y = reader.GetFloat();
        }
    }
}
