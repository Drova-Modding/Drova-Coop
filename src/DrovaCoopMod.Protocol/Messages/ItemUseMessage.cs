using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// That a player started, or stopped, using one of their items - an ability, or something they drink,
    /// eat or throw.
    ///
    /// The name says ability because that is what it was built for, and the id it travels under must not
    /// change; consumables joined it later because they use the identical funnel and want the identical
    /// treatment. What is not here is anything whose use is not a gesture - a recipe learned, a letter
    /// read - because replicating those would open a window on somebody else's screen.
    ///
    /// Abilities do not travel as actions. Everything else a player does in combat reaches the wire
    /// as an <c>EActionBind</c>, but that vocabulary has eight members and none of them is a spell: the
    /// game casts through <c>Item.StartUseItem</c> on the selected ability item instead, entirely beside
    /// the action bindings. Without this message a remote player throwing a fireball is a body standing
    /// still, because nothing about the cast is in anything else that crosses.
    ///
    /// What crosses is the cast, not the spell. The far side hands the same item to its copy of the
    /// caster and lets the game do the rest, so the animation, the projectile, its trail and its impact
    /// are all reproduced from the receiving machine's own assets rather than described here. That is
    /// what keeps this to a string and two bytes, and it is also why the ability's own id is the only
    /// thing worth sending: everything below it is the same on both machines.
    ///
    /// The spell that results deals no damage. It cannot, or every hit would count twice - the
    /// caster's machine already resolved this spell against its copy of whoever it hit and reported the
    /// result as an ordinary blow. See <c>ItemUseWatch</c>, which suppresses the damage on a body that
    /// belongs to somebody else.
    ///
    /// Sent reliably and only on the edges, like equipment and unlike player state: a cast is an event
    /// rather than a value being sampled, so a lost one is not corrected by the next - it is a spell that
    /// never appeared.
    /// </summary>
    public struct ItemUseMessage : INetMessage
    {
        /// <summary>
        /// The longest ability id accepted. Real ids are short and this is generous, which is the point:
        /// it costs nothing and it stops an id being a way to put a kilobyte of someone else's text into
        /// this machine's log.
        /// </summary>
        public const int MaxItemUseIdLength = 64;

        /// <summary>
        /// Who cast it. As everywhere else, the host overwrites a client's claim with the id it assigned
        /// that peer, so nobody can cast as anybody else.
        /// </summary>
        public PlayerId User;

        /// <summary>
        /// The readable id of the ability item, resolved on the far side through the item database the
        /// same way equipment is. Never null after a read.
        /// </summary>
        public string ItemId;

        /// <summary>
        /// Which cast this is, wrapping at a byte. What tells a fresh cast from the same one arriving
        /// twice, and what lets the receiver ignore a start that overtook its own release.
        /// </summary>
        public byte Sequence;

        /// <summary>
        /// Zero while the cast is held, one when it has been let go.
        ///
        /// A separate field rather than a second sequence, because letting go is the same cast
        /// continuing: a release that counted as a new cast would have the far body cast twice, which is
        /// the mistake the action binds already made once.
        /// </summary>
        public byte Released;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(User.Value);
            writer.Put(Sequence);
            writer.Put(Released);
            writer.Put(WireStrings.Cut(ItemId, MaxItemUseIdLength));
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            User = new PlayerId(reader.GetByte());
            Sequence = reader.GetByte();
            Released = reader.GetByte();

            // Truncated on read as well as on write, because the sender is not the one to trust about how
            // long its own strings are.
            ItemId = WireStrings.Cut(reader.GetString(), MaxItemUseIdLength);
        }
    }
}
