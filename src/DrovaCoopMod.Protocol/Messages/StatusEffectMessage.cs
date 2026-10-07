using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A status effect one machine put on another player's puppet, forwarded to the body's owner. Sent reliably.
    /// Bleeding travels as a flag on <see cref="DamageReportMessage"/> instead, since only the victim knows the damage that landed.
    /// </summary>
    public struct StatusEffectMessage : INetMessage
    {
        /// <summary>The longest effect guid accepted, a bound on untrusted text.</summary>
        public const int MaxEffectIdLength = 64;

        /// <summary>Who the effect belongs on. The host overwrites a client's claim with that peer's id.</summary>
        public PlayerId Target;

        /// <summary>Who applied it, so stacking effects stack per attacker.</summary>
        public PlayerId Source;

        /// <summary>The status effect asset guid. Never null after a read.</summary>
        public string EffectId;

        /// <summary>Wrapping counter that tells a repeat from a second stack.</summary>
        public byte Sequence;

        /// <summary>
        /// <see cref="DamageReportMessage.OriginPlayer"/> or <see cref="DamageReportMessage.OriginCreature"/>,
        /// for the friendly-fire rule.
        /// </summary>
        public byte Origin;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Target.Value);
            writer.Put(Source.Value);
            writer.Put(Sequence);
            writer.Put(Origin);
            writer.Put(WireStrings.Cut(EffectId, MaxEffectIdLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Target = new PlayerId(reader.GetByte());
            Source = new PlayerId(reader.GetByte());
            Sequence = reader.GetByte();

            // Unrecognized reads as a player's, which keeps the friendly-fire rule.
            byte origin = reader.GetByte();
            Origin = origin == DamageReportMessage.OriginCreature
                ? DamageReportMessage.OriginCreature
                : DamageReportMessage.OriginPlayer;

            EffectId = WireStrings.Cut(reader.GetString(), MaxEffectIdLength);
        }
    }
}
