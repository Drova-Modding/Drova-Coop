using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>Players already in the session, sent by the host to a client it has just accepted, in chunks the client merges.</summary>
    public struct RosterSnapshotMessage : INetMessage
    {
        /// <summary>The most players one message may describe, enforced on read. With the longest names it stays under the relay's 1170 bytes.</summary>
        public const int MaxPlayers = 8;

        /// <summary>Who is in, excluding the client being told. Never null after a read.</summary>
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

    /// <summary>One player in a roster.</summary>
    /// <param name="Id">The id the host assigned them.</param>
    /// <param name="Name">What to call them.</param>
    public readonly record struct RosterEntry(PlayerId Id, string Name);
}
