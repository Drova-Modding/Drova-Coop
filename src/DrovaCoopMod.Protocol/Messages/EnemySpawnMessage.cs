using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A runtime-spawned creature, whose guid is process-local, rebuilt elsewhere from its asset under the host's handle.
    /// Anything rolled while building it differs per machine.
    /// </summary>
    public struct EnemySpawnMessage : INetMessage
    {
        /// <summary>The longest asset id accepted.</summary>
        public const int MaxAssetLength = 64;

        /// <summary>The handle this creature will be known by.</summary>
        public EnemyNetId NetId;

        /// <summary>Which asset to build it from. Never null after a read.</summary>
        public string AssetGuid;

        /// <summary>Where to put it.</summary>
        public float X;

        /// <summary>See <see cref="X"/>.</summary>
        public float Y;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(NetId.Value);
            writer.Put(WireStrings.Cut(AssetGuid, MaxAssetLength));
            writer.Put(X);
            writer.Put(Y);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());
            AssetGuid = WireStrings.Cut(reader.GetString(), MaxAssetLength);
            X = reader.GetFloat();
            Y = reader.GetFloat();
        }

        /// <summary>Whether the position is finite and in range. A spawn is never corrected by a later update.</summary>
        public readonly bool IsPlausible()
        {
            return Within(X) && Within(Y) && !string.IsNullOrEmpty(AssetGuid);
        }

        private static bool Within(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;

            return value >= -PlayerStateMessage.MaxCoordinate && value <= PlayerStateMessage.MaxCoordinate;
        }
    }
}
