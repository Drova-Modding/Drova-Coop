using Drova_Modding_API.Systems.Networking;

namespace DrovaCoopMod.Messages
{
    /// <summary>
    /// One player inviting another, from the talk menu on the teammate's body: a duel, or a place in the inviter's arena
    /// team. Asked (<see cref="StageAsk"/>), answered with yes or no; what a yes starts is the host's to do.
    ///
    /// Client to host, host on to <see cref="To"/>; the host stamps <see cref="From"/> from the connection.
    /// </summary>
    public struct InviteMessage : INetMessage
    {
        /// <summary>A duel: an arena between the two of them, where they stand.</summary>
        public const byte KindDuel = 0;

        /// <summary>Join the inviter's arena team.</summary>
        public const byte KindTeam = 1;

        /// <summary>Inviter to invitee.</summary>
        public const byte StageAsk = 0;

        /// <summary>Invitee to inviter: yes.</summary>
        public const byte StageYes = 1;

        /// <summary>Invitee to inviter: no.</summary>
        public const byte StageNo = 2;

        /// <summary>One of the Kind constants.</summary>
        public byte Kind;

        /// <summary>One of the Stage constants.</summary>
        public byte Stage;

        /// <summary>Who sent it. Stamped by the host.</summary>
        public PlayerId From;

        /// <summary>Who it is for.</summary>
        public PlayerId To;

        /// <inheritdoc/>
        public readonly void Write(INetWriter writer)
        {
            writer.Put(Kind);
            writer.Put(Stage);
            writer.Put(From.Value);
            writer.Put(To.Value);
        }

        /// <inheritdoc/>
        public void Read(INetReader reader)
        {
            Kind = reader.GetByte();
            Stage = reader.GetByte();
            From = new PlayerId(reader.GetByte());
            To = new PlayerId(reader.GetByte());
        }
    }
}
