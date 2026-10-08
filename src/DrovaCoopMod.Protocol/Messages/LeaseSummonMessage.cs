using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Client to host: a creature conjured in the client's leased fight. The host builds and announces it,
    /// and the client binds its own summon to that announcement by prefab and spot.
    /// </summary>
    public struct LeaseSummonMessage : INetMessage
    {
        /// <summary>The longest prefab name carried.</summary>
        private const int MaxPrefabLength = 64;

        /// <summary>The summon's prefab, by name.</summary>
        public string Prefab;

        /// <summary>Where it appeared.</summary>
        public float X;

        /// <summary>See <see cref="X"/>.</summary>
        public float Y;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(WireStrings.Cut(Prefab, MaxPrefabLength));
            writer.Put(X);
            writer.Put(Y);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Prefab = WireStrings.Cut(reader.GetString(), MaxPrefabLength);
            X = reader.GetFloat();
            Y = reader.GetFloat();
        }
    }
}
