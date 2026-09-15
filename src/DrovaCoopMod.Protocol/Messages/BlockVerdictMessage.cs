using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What the defender's own game decided about one blow: whether the guard held, broke, or caught it
    /// perfectly.
    ///
    /// The answer to a <see cref="DamageReportMessage"/>, and it travels the other way. The blow already
    /// crosses rather than the wound, so the victim's own armor and guard settle the damage where the
    /// numbers are real. What does not cross is the verdict, and the attacker's machine works it out
    /// again from a puppet: a body that mirrors a stamina pool one round trip stale and re-runs a
    /// calculation it has no business running. So the same swing can hold over there and break over here,
    /// and both players watch a different fight.
    ///
    /// The blow is named by the attacker and the sequence number the report already carries, so there is
    /// no new identity to invent and no window to guess at. A verdict for a blow the attacker has
    /// forgotten is dropped, which is what happens when one arrives late.
    /// </summary>
    public struct BlockVerdictMessage : INetMessage
    {
        /// <summary>The blow landed and the guard had nothing to do with it.</summary>
        public const byte VerdictNone = 0;

        /// <summary>The guard held and took the blow.</summary>
        public const byte VerdictBlocked = 1;

        /// <summary>The guard caught it inside the perfect window.</summary>
        public const byte VerdictPerfect = 2;

        /// <summary>The guard could not afford the blow and broke.</summary>
        public const byte VerdictBroken = 3;

        /// <summary>
        /// Who swung. Overwritten by the host with the id it assigned the sending peer, the same way the
        /// report itself is, so nobody can answer for a blow that was not aimed at them.
        /// </summary>
        public PlayerId Attacker;

        /// <summary>Who was struck, and therefore who is answering.</summary>
        public PlayerId Victim;

        /// <summary>
        /// Which blow, as <see cref="DamageReportMessage.Sequence"/> numbered it. The pair of attacker
        /// and sequence is already unique for the length of a session.
        /// </summary>
        public uint Sequence;

        /// <summary>One of the four verdicts. Anything else reads as <see cref="VerdictNone"/>.</summary>
        public byte Verdict;

        /// <summary>
        /// Whether this names a verdict the attacker could act on. A blow answers to exactly one
        /// attacker and one victim, and they are never the same player.
        /// </summary>
        public readonly bool IsPlausible()
        {
            return Attacker != Victim && Verdict <= VerdictBroken;
        }

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Attacker.Value);
            writer.Put(Victim.Value);
            writer.Put(Sequence);
            writer.Put(Verdict);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Attacker = new PlayerId(reader.GetByte());
            Victim = new PlayerId(reader.GetByte());
            Sequence = reader.GetUInt();

            byte verdict = reader.GetByte();
            Verdict = verdict <= VerdictBroken ? verdict : VerdictNone;
        }
    }
}
