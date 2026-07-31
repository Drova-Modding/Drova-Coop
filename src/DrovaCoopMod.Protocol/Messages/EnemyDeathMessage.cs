using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// That something which is not a player died, so it stays dead for everybody.
    ///
    /// Every machine runs its own copy of every enemy, and until they share something they disagree
    /// about all of it. Death is the first thing worth agreeing on and the cheapest, because it is the one
    /// fact that is permanent: an enemy killed on one machine and left standing on the other is a
    /// disagreement that never resolves, gets saved, and is still there tomorrow. Health can be wrong for a
    /// second and correct itself; a living corpse cannot.
    ///
    /// Named by the guid its scene was authored with, not by an id this session made up. Every actor
    /// carries a <c>GuidComponent</c> whose string is a serialized field baked into the scene asset, and
    /// the game keys its own save files on it - which is the proof that it means the same thing in two
    /// processes, since a save written by one and read by the other already depends on it.
    ///
    /// Everyone, both directions, like <see cref="KnowledgeMessage"/> and unlike the blows and effects
    /// around it. Whoever's machine saw the thing die says so; there is no victim to address it to.
    ///
    /// Reliable and idempotent. Applying it twice is a health write to something already at zero, which
    /// the game refuses on its own; losing it is an enemy that is alive on one screen forever.
    /// </summary>
    public struct EnemyDeathMessage : INetMessage
    {
        /// <summary>
        /// The longest guid accepted. A real one is 36 characters - the length
        /// <c>GuidComponent.CreateGuid</c> itself checks for - and this is that length exactly, because
        /// unlike an item id there is no version of this string that is legitimately longer.
        /// </summary>
        public const int MaxGuidLength = 36;

        /// <summary>
        /// Set when the sender is reporting a death it did not witness but merely restored - a corpse that
        /// was already dead in the save, arriving as the world streams in.
        ///
        /// The distinction is not bookkeeping. The receiver passes it to the game as
        /// <c>HealthChangeArgs.SetIsLoadingArg</c>, which is the game's own switch for the same problem:
        /// a body restored from a save must not replay its death animation, its ragdoll, its experience
        /// award or the ability some creatures cast as they die. A death that happens in front of a player
        /// must do all of those.
        ///
        /// Zero means a real death, which is the safe default for a field that could arrive from a
        /// peer that never set it.
        /// </summary>
        public const byte FlagReconcile = 1 << 0;

        /// <summary>
        /// Which actor died, as the guid string of its <c>GuidComponent</c>. Never null after a read.
        /// </summary>
        public string ActorGuid;

        /// <summary>
        /// Flags. See <see cref="FlagReconcile"/>. Unknown bits are ignored rather than refused, so a peer
        /// on a later version can add one without this build discarding the death itself.
        /// </summary>
        public byte Flags;

        /// <summary>Whether this death is a save being reconciled rather than a kill.</summary>
        public readonly bool IsReconcile => (Flags & FlagReconcile) != 0;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Truncate(ActorGuid ?? string.Empty));
            writer.Put(Flags);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            // Truncated on read as well as on write, because the sender is not the one to trust about how
            // long its own strings are.
            ActorGuid = Truncate(reader.GetString() ?? string.Empty);
            Flags = reader.GetByte();
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxGuidLength ? value : value[..MaxGuidLength];
        }
    }
}
