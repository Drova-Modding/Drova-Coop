using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A creature conjured in a client's leased fight (an arena champion's rippers, insects, doppelgangers). Client to host:
    /// the host builds the same prefab at the spot, leases it to that client and announces it like any spawn; the client
    /// recognises its own summon in that announcement (same prefab, same spot) and binds it to the host's handle.
    /// </summary>
    public struct LeaseSummonMessage : INetMessage
    {
        /// <summary>The longest prefab name carried.</summary>
        public const int MaxPrefabLength = 64;

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
