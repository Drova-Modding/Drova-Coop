using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Whether the host moves one spawned creature's body, said on its own rather than inside a bind.
    ///
    /// Authored creatures do not need this: who drives them travels as a flag on the bind, and a
    /// changed answer is sent as a fresh bind. A spawned creature cannot be re-bound, because a bind
    /// names a guid and a spawned creature's guid was minted on the machine that built it - sending one
    /// would hand the far side a name it can never resolve, and the far side files it into the guid
    /// bookkeeping that the corpse and rebirth paths read. So the one fact that changes over time
    /// crosses alone, under the handle both machines already share.
    ///
    /// Why the answer changes at all: who drives is a distance from the host's own player, and both
    /// ends of that distance walk around. A summon fighting beside its caster on the other side of the
    /// map is better left running the caster's own machine's behavior - the host is not close enough to
    /// its copy to have an opinion worth forcing on the only screen that is watching.
    /// </summary>
    public struct EnemyDriveMessage : INetMessage
    {
        /// <summary>Which creature.</summary>
        public EnemyNetId NetId;

        /// <summary>Whether the host moves its body. Its health is the host's either way.</summary>
        public bool HostDrives;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(NetId.Value);
            writer.Put(HostDrives);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            NetId = new EnemyNetId(reader.GetUShort());
            HostDrives = reader.GetBool();
        }
    }
}
