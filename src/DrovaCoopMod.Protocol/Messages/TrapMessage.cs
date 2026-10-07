using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A trap laid or gone on the machine running the fight. Other machines show an inert copy at the same spot.
    /// </summary>
    public struct TrapMessage : INetMessage
    {
        /// <summary>The longest prefab name carried.</summary>
        public const int MaxPrefabLength = 64;

        /// <summary><see cref="Prefab"/> now stands at the spot.</summary>
        public const byte OpPlaced = 0;

        /// <summary>The trap at the spot fired, was disarmed or expired.</summary>
        public const byte OpGone = 1;

        /// <summary><see cref="OpPlaced"/> or <see cref="OpGone"/>.</summary>
        public byte Op;

        /// <summary>The trap's prefab name. Empty for <see cref="OpGone"/>.</summary>
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
