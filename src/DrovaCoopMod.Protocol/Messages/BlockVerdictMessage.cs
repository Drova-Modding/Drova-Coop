using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// The defender's guard verdict on one blow, answering a <see cref="DamageReportMessage"/>.
    /// The attacker's puppet sees stale stamina, so only the victim's machine can decide it.
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

        /// <summary>Who swung. The host overwrites it with the sending peer's id, so it cannot be spoofed.</summary>
        public PlayerId Attacker;

        /// <summary>Who was struck, and therefore who is answering.</summary>
        public PlayerId Victim;

        /// <summary>Which blow, as <see cref="DamageReportMessage.Sequence"/> numbered it.</summary>
        public uint Sequence;

        /// <summary>One of the four verdicts. Anything else reads as <see cref="VerdictNone"/>.</summary>
        public byte Verdict;

        /// <summary>Whether attacker and victim differ and the verdict is known.</summary>
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
