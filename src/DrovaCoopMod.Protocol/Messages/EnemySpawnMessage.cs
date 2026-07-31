using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A creature that was put into the world at runtime rather than authored into a scene, so the other
    /// machines can produce it too.
    ///
    /// Everything else in this protocol names a creature by the guid its scene was authored with, which
    /// both machines read out of the same asset. A creature spawned by a mod or a wave spawner has no such
    /// name: its guid is minted fresh in the process that made it and means nothing anywhere else. So the
    /// spawn itself has to cross, carrying the one thing that does mean the same on both machines - the
    /// asset it was made from - and the host's handle for it.
    ///
    /// After this arrives, that creature is an ordinary member of the session: its health, its position and
    /// its death all travel under the handle, exactly as an authored creature's do.
    ///
    /// What crosses is the recipe, not the creature. Two machines build from the same asset, so
    /// anything decided while building - a randomised name, a rolled-up set of equipment - is decided
    /// independently and will differ. That is a real limit, and it is why this carries an asset rather than
    /// pretending to carry a creature.
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
            writer.Put(Truncate(AssetGuid ?? string.Empty));
            writer.Put(X);
            writer.Put(Y);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());
            AssetGuid = Truncate(reader.GetString() ?? string.Empty);
            X = reader.GetFloat();
            Y = reader.GetFloat();
        }

        /// <summary>
        /// Whether this names somewhere a creature could be put.
        ///
        /// Checked because a spawn is acted on rather than superseded: an implausible position would put a
        /// live creature somewhere nothing can reach, and unlike a position update nothing arrives a
        /// moment later to correct it.
        /// </summary>
        /// <returns>False when the position is not finite and plausible.</returns>
        public readonly bool IsPlausible()
        {
            return Within(X) && Within(Y) && !string.IsNullOrEmpty(AssetGuid);
        }

        private static bool Within(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return false;

            return value >= -PlayerStateMessage.MaxCoordinate && value <= PlayerStateMessage.MaxCoordinate;
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxAssetLength ? value : value[..MaxAssetLength];
        }
    }
}
