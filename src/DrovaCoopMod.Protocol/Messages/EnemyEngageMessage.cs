using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A client telling the host that its player is personally engaged with one of the host's NPCs -
    /// talking to it, or fighting it in a scripted brawl.
    ///
    /// Both engagements are staged by graphs that run only on the machine whose player stands in them,
    /// against a body whose brain lives on the host. Without this message the host's copy has no idea
    /// the scene is happening: it walks a dialogue partner away mid-sentence, and it never learns that
    /// an attacker is a duel opponent rather than a murderer - which is how a brawl the client started
    /// became an NPC that fought a downed player forever.
    ///
    /// Sent on every transition and repeated while the engagement lasts, so the host can expire an
    /// engagement whose client died without saying goodbye. Reliable, because a lost transition is a
    /// held NPC nobody releases.
    /// </summary>
    public struct EnemyEngageMessage : INetMessage
    {
        /// <summary>The engagement ended.</summary>
        public const byte ModeNone = 0;

        /// <summary>The player is in a dialogue with the NPC.</summary>
        public const byte ModeDialogue = 1;

        /// <summary>The player is in a scripted brawl with the NPC.</summary>
        public const byte ModeBrawl = 2;

        /// <summary>
        /// Whose player is engaged. The host overwrites a client's claim with the id it assigned that
        /// peer, so a client cannot speak for somebody else.
        /// </summary>
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
