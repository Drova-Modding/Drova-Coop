using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// Everyone already in the session, sent by the host to a client it has just accepted. Without it a
    /// joining player knows only about the host and would never see anyone who arrived first.
    ///
    /// Sent once per join, so the allocation for the names is fine here in a way it would not be in
    /// anything sent per frame.
    /// </summary>
    public struct RosterSnapshotMessage : INetMessage
    {
        /// <summary>
        /// The most players a roster may describe. A session holds a handful, and this is a bound on what
        /// a peer can make the receiver allocate, so it is enforced on read rather than trusted.
        /// </summary>
        public const int MaxPlayers = 8;

        /// <summary>
        /// Who is in, excluding the client being told. Never null after a read.
        /// </summary>
        public RosterEntry[] Players;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            RosterEntry[] players = Players ?? [];
            byte count = (byte)Math.Min(players.Length, MaxPlayers);

            writer.Put(count);
            for (int index = 0; index < count; index++)
            {
                writer.Put(players[index].Id.Value);
                writer.Put(WireStrings.Cut(players[index].Name, HandshakeMessage.MaxPlayerNameLength));
            }
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            int count = Math.Min((int)reader.GetByte(), MaxPlayers);
            Players = new RosterEntry[count];

            for (int index = 0; index < count; index++)
            {
                PlayerId id = new(reader.GetByte());
                Players[index] = new RosterEntry(id, WireStrings.Cut(reader.GetString(), HandshakeMessage.MaxPlayerNameLength));
            }
        }
    }

    /// <summary>
    /// One player in a roster.
    /// </summary>
    /// <param name="Id">The id the host assigned them.</param>
    /// <param name="Name">What to call them.</param>
    public readonly record struct RosterEntry(PlayerId Id, string Name);
}
