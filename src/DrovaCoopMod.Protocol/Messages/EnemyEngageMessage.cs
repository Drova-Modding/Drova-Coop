using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A client telling the host its player is in a dialogue or scripted brawl with a host NPC, whose graph runs only on the client.
    /// Repeated while it lasts, so the host can expire an engagement whose client vanished.
    /// </summary>
    public struct EnemyEngageMessage : INetMessage
    {
        /// <summary>The engagement ended.</summary>
        public const byte ModeNone = 0;

        /// <summary>The player is in a dialogue with the NPC.</summary>
        public const byte ModeDialogue = 1;

        /// <summary>The player is in a scripted brawl with the NPC.</summary>
        public const byte ModeBrawl = 2;

        /// <summary>Whose player is engaged. Stamped by the host from the peer.</summary>
        public PlayerId Player;

        /// <summary>Which NPC.</summary>
        public EnemyNetId NetId;

        /// <summary>One of the Mode constants.</summary>
        public byte Mode;

        /// <summary>Whether the mode is one this build knows.</summary>
        public readonly bool IsPlausible()
        {
            return Mode is ModeNone or ModeDialogue or ModeBrawl;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Player.Value);
            writer.Put(NetId.Value);
            writer.Put(Mode);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Player = new PlayerId(reader.GetByte());
            NetId = new EnemyNetId(reader.GetUShort());
            Mode = reader.GetByte();
        }
    }
}
