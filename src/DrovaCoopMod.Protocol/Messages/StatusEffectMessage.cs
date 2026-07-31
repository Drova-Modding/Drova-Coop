using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// A status effect one player's machine put on another player's body.
    ///
    /// **Effects are decided where the blow is struck, and felt where the character lives.** Poison,
    /// wounds, slows and everything else are applied by whatever landed the hit, on the machine that
    /// landed it - which for a remote player means the effect goes onto a puppet, a body that has no
    /// character behind it and cannot be poisoned in any way its owner would notice. What the owner sees
    /// instead is their health quietly draining, because the puppet's effect ticks damage and that damage
    /// is reported as ordinary blows. Damage without a cause, and no icon to explain it.
    ///
    /// **The effect is named by its asset guid.** Status effects are authored assets rather than an enum,
    /// so there is nothing smaller to send; the guid is what the game's own database is keyed by, and the
    /// far side resolves it the same way equipment resolves item ids. An effect this machine does not
    /// have is a mod-set difference and is named in the log rather than guessed at.
    ///
    /// **Bleeding deliberately does not travel this way.** It is applied by the health change itself
    /// rather than by an ability, and its severity depends on the damage that actually landed - which
    /// only the victim's machine knows. It crosses as a flag on <see cref="DamageReportMessage"/> so the
    /// victim's own game can size the wound, and sending it here as well would open a second one.
    ///
    /// Sent reliably. An effect is an event, so a lost one is not corrected by the next.
    /// </summary>
    public struct StatusEffectMessage : INetMessage
    {
        /// <summary>
        /// The longest guid accepted. Real ones are a fixed short length and this is generous, which is
        /// the point: it costs nothing and it stops a guid being a way to put a kilobyte of someone
        /// else's text into this machine's log.
        /// </summary>
        public const int MaxEffectIdLength = 64;

        /// <summary>
        /// Who the effect belongs on. The host overwrites a client's claim with the id it assigned that
        /// peer, so a peer can only ever say what happened on its own machine.
        /// </summary>
        public PlayerId Target;

        /// <summary>
        /// Who applied it. Carried so the receiver can tell one attacker's poison from another's, and so
        /// a stacking effect stacks per attacker the way it does locally.
        /// </summary>
        public PlayerId Source;

        /// <summary>
        /// The guid of the status effect asset, resolved on the far side through the game's own status
        /// effect database. Never null after a read.
        /// </summary>
        public string EffectId;

        /// <summary>
        /// Which effect this is, wrapping at a byte. Effects are the one thing here that is not
        /// idempotent - applying the same poison twice is two stacks - so a repeat has to be
        /// distinguishable from a second application.
        /// </summary>
        public byte Sequence;

        /// <summary>
        /// What applied it: <see cref="DamageReportMessage.OriginPlayer"/> or
        /// <see cref="DamageReportMessage.OriginCreature"/>.
        ///
        /// Read by the friendly-fire rule for the same reason the damage report's is. A creature's poison
        /// reaches its victim through this message because the creature only swings on the machine that
        /// drives it, and a session with friendly fire off should not turn that into immunity.
        /// </summary>
        public byte Origin;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Target.Value);
            writer.Put(Source.Value);
            writer.Put(Sequence);
            writer.Put(Origin);
            writer.Put(Truncate(EffectId ?? string.Empty));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Target = new PlayerId(reader.GetByte());
            Source = new PlayerId(reader.GetByte());
            Sequence = reader.GetByte();

            // Unrecognized reads as a player's doing, which keeps the friendly-fire rule over anything
            // this build cannot classify.
            byte origin = reader.GetByte();
            Origin = origin == DamageReportMessage.OriginCreature
                ? DamageReportMessage.OriginCreature
                : DamageReportMessage.OriginPlayer;

            // Truncated on read as well as on write, because the sender is not the one to trust about how
            // long its own strings are.
            EffectId = Truncate(reader.GetString() ?? string.Empty);
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxEffectIdLength ? value : value[..MaxEffectIdLength];
        }
    }
}
