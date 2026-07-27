using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// What a player is wearing and carrying, as the ids of the items in their active equipment set.
    ///
    /// **Equipment is not decoration.** Equipping a weapon is what binds that weapon's action to
    /// <c>PrimaryAttack</c> - that is what <c>ItemBhvr_EquipEffect_ActionBinding</c> does - so a body
    /// holding nothing has the bare-handed action bound instead. Replicating an attack to such a body
    /// faithfully plays the wrong attack: the far player swings a spear and the body throws a punch,
    /// with the punch's own combo chain. Until the body holds the same thing, no weapon-specific
    /// combat can replicate correctly.
    ///
    /// Readable ids rather than anything structural. They are plain strings, so nothing about the game's
    /// types reaches the wire, and the far side resolves them through the item database the same way the
    /// API's own NPC spawner does. Slots are deliberately not sent: which slot an item belongs in is a
    /// property of the item, and letting each machine work that out itself is both shorter and more
    /// robust than agreeing on slot indices.
    ///
    /// Sent reliably and only when it changes. Equipment changes when a player decides it does, which is
    /// rare and never per frame, so this is the opposite case to <see cref="PlayerStateMessage"/>: losing
    /// one is not covered by the next, because there may not be a next for a long time.
    /// </summary>
    public struct PlayerEquipmentMessage : INetMessage
    {
        /// <summary>
        /// The most items one player's active set may describe. A set holds a handful of slots; this is a
        /// bound on what a peer can make the receiver allocate, so it is enforced on read rather than
        /// trusted.
        /// </summary>
        public const int MaxItems = 16;

        /// <summary>
        /// The longest item id accepted. Real ids are short and this is generous, which is the point: it
        /// costs nothing and it stops an id being a way to put a kilobyte of someone else's text into
        /// this machine's log.
        /// </summary>
        public const int MaxItemIdLength = 64;

        /// <summary>
        /// Whose equipment this is. As everywhere else, the host overwrites a client's claim with the id
        /// it assigned that peer, so nobody can dress anybody else.
        /// </summary>
        public PlayerId PlayerId;

        /// <summary>
        /// The readable ids of every item in the sender's active set. Never null after a read.
        /// </summary>
        public string[] ItemIds;

        /// <summary>
        /// The sender's build, as <c>ActorBodyType.BodyType</c>.
        ///
        /// **Not equipment, and that is why it has to be sent separately.** A player's build is a component
        /// on the actor rather than something worn, so a body cloned from the player prefab keeps the
        /// prefab's build no matter what it is dressed in. The skin item then makes it worse rather than
        /// better: it only sets a skin id *on whatever build the actor has*, so the wrong build wears the
        /// right skin and every piece of hair, beard and scar sits where it does not belong.
        /// </summary>
        public byte BodyType;

        /// <summary>
        /// The sender's voice, as <c>VoiceID</c>. It travels with the build because it is chosen with it
        /// and lives on the same component, and a body that sounds like somebody else is the same class
        /// of wrong as one that looks like them.
        /// </summary>
        public byte VoiceId;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            string[] ids = ItemIds ?? [];
            byte count = (byte)Math.Min(ids.Length, MaxItems);

            writer.Put(PlayerId.Value);
            writer.Put(BodyType);
            writer.Put(VoiceId);
            writer.Put(count);

            for (int index = 0; index < count; index++)
            {
                writer.Put(Truncate(ids[index] ?? string.Empty));
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            PlayerId = new PlayerId(reader.GetByte());
            BodyType = reader.GetByte();
            VoiceId = reader.GetByte();

            int count = Math.Min((int)reader.GetByte(), MaxItems);
            ItemIds = new string[count];

            for (int index = 0; index < count; index++)
            {
                // Truncated on read as well as on write, because the sender is not the one to trust about
                // how long its own strings are.
                ItemIds[index] = Truncate(reader.GetString() ?? string.Empty);
            }
        }

        private static string Truncate(string value)
        {
            return value.Length <= MaxItemIdLength ? value : value[..MaxItemIdLength];
        }
    }
}
