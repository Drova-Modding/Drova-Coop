using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// A place where the party fights each other on purpose.
    ///
    /// Friendly fire is one switch for the whole session, which is the right shape for an accident
    /// between allies and the wrong shape for a duel. A party that wants to settle something should not
    /// have to leave itself open to every stray swing for the rest of the evening, so the arena makes it
    /// a place instead: inside the circle players can hurt each other whatever the session rule says, and
    /// walking out restores the rule they were playing under. Nothing is added to the damage path for it -
    /// the rule the path already consults simply answers per pair rather than once.
    ///
    /// The host marks the circle where they are standing, because a mod cannot author a scene. It is the
    /// host's to move and to take down, and it crosses to everyone so both ends agree who is inside.
    ///
    /// The host also arbitrates the round, and that is the one piece of authority in this protocol that
    /// is not about a character. Every machine knows its own player's health and takes everybody else's
    /// on trust: fine between friends, not fine for a contest somebody will argue about, because two
    /// players can each believe they were the one left standing. The host reads the downed flags it is
    /// already being sent and says who went down. Ownership of the bodies does not move.
    ///
    /// What this is not: lag compensation, or a fair-fight guarantee. The defender still reacts to a
    /// swing committed a round trip ago, and the roadmap's symmetric input delay is deliberately not here
    /// - that is a decision to make once a duel works, not before.
    /// </summary>
    internal static class Arena
    {
        /// <summary>How often the host looks at who is inside and whether a round has ended.</summary>
        private const float ArbitrateSeconds = 0.25f;

        private static Vector2 _center;

        private static ushort _round;

        /// <summary>Who the host has already called a round against, so one knockdown ends one round.</summary>
        private static PlayerId _calledAgainst = PlayerId.None;

        private static float _sinceArbitrate;

        /// <summary>Whether an arena is marked.</summary>
        internal static bool Active { get; private set; }

        /// <summary>Where it is. Meaningless while <see cref="Active"/> is false.</summary>
        internal static Vector2 Center => _center;

        /// <summary>How far it reaches.</summary>
        internal static float Radius { get; private set; }

        /// <summary>Which round the arena is on.</summary>
        internal static ushort Round => _round;

        /// <summary>How many rounds this machine has been told the result of.</summary>
        internal static int Rounds { get; private set; }

        /// <summary>Takes the arena down and forgets it. Called when a session ends.</summary>
        internal static void Reset()
        {
            Active = false;
            _center = Vector2.zero;
            Radius = 0f;
            _round = 0;
            Rounds = 0;
            _calledAgainst = PlayerId.None;
            _sinceArbitrate = 0f;
        }

        /// <summary>
        /// Whether these two players may hurt each other right now.
        ///
        /// The session rule first, because a party that turned friendly fire on meant it everywhere. Then
        /// the arena, which only ever adds: standing in it never makes anybody safer than the session
        /// already had them.
        /// </summary>
        /// <param name="other">The other player in the pair. The local player is the implied first.</param>
        internal static bool AllowsHarm(PlayerId other)
        {
            if (CoopSession.FriendlyFire) return true;
            if (!Active) return false;

            return HoldsLocal() && Holds(other);
        }

        /// <summary>Whether a player's body is inside the circle. False when it is not here to ask.</summary>
        internal static bool Holds(PlayerId player)
        {
            if (!Active) return false;

            if (player == CoopSession.LocalPlayerId) return HoldsLocal();

            if (!RemotePlayers.Bodies.TryGetValue(player, out RemotePlayer? body)) return false;
            if (body == null || body.Body == null) return false;

            return Contains(body.Body.transform.position);
        }

        /// <summary>Whether this machine's own player is inside.</summary>
        internal static bool HoldsLocal()
        {
            if (!Active) return false;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return false;

            return Contains(player.transform.position);
        }

        /// <summary>
        /// Marks the arena where the host is standing. Host only.
        /// </summary>
        /// <param name="radius">How far it should reach.</param>
        /// <returns>What to tell the player, whether or not it worked.</returns>
        internal static string Mark(float radius)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return "only the host can mark an arena.";
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return "no player to mark it around.";

            float clamped = Mathf.Clamp(radius, ArenaStateMessage.MinRadius, ArenaStateMessage.MaxRadius);

            Vector3 where = player.transform.position;
            _center = new Vector2(where.x, where.y);
            Radius = clamped;
            Active = true;
            _round = 1;
            _calledAgainst = PlayerId.None;

            Broadcast();

            return $"arena marked here, {clamped:0.#} units across the middle. Friendly fire is on inside it " +
                "whatever the session rule says, and off again outside.";
        }

        /// <summary>Takes the arena down. Host only.</summary>
        internal static string Clear()
        {
            if (CoopSession.State != CoopSessionState.Hosting) return "only the host can take the arena down.";
            if (!Active) return "there is no arena.";

            Active = false;
            _round = 0;
            _calledAgainst = PlayerId.None;
            Broadcast();

            return "arena taken down; the session's own friendly fire rule is what counts again.";
        }

        /// <summary>Sends the arena to one joining player. Host only.</summary>
        internal static void SendTo(Drova_Modding_API.Systems.Networking.INetPeer peer)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            CoopSession.SendArenaState(peer, Describe());
        }

        /// <summary>Takes the host's word for where the arena is.</summary>
        internal static void Take(in ArenaStateMessage message)
        {
            if (!message.IsPlausible()) return;

            bool wasActive = Active;

            Active = message.Active != 0;
            _center = new Vector2(message.CenterX, message.CenterY);
            Radius = message.Radius;

            if (Active == wasActive) return;

            MelonLogger.Msg(Active
                ? $"[Arena] an arena is marked, {Radius:0.#} units across. Friendly fire is on inside it."
                : "[Arena] the arena is gone; the session's own friendly fire rule is what counts again.");
        }

        /// <summary>Takes the host's word on how a round ended.</summary>
        internal static void TakeRound(in ArenaRoundMessage message)
        {
            if (!message.IsPlausible()) return;

            Rounds++;
            _round = (ushort)(message.Round + 1);

            string who = message.Loser == CoopSession.LocalPlayerId ? "you went down" : $"player {message.Loser.Value} went down";
            MelonLogger.Msg($"[Arena] round {message.Round} to the others: {who}, {message.Fighters} in the circle.");
        }

        /// <summary>
        /// Host only: watches the circle and calls a round when somebody in it goes down.
        ///
        /// Read off the downed flags every player already sends rather than asking for anything new, so
        /// there is nothing here a client could lie about that it could not already lie about.
        /// </summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (!Active || CoopSession.State != CoopSessionState.Hosting) return;

            _sinceArbitrate += unscaledDeltaTime;
            if (_sinceArbitrate < ArbitrateSeconds) return;

            _sinceArbitrate = 0f;

            try
            {
                Arbitrate();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] calling a round failed: " + e);
            }
        }

        private static void Arbitrate()
        {
            int inside = 0;
            PlayerId down = PlayerId.None;

            if (HoldsLocal())
            {
                inside++;
                if (DownedState.IsDown) down = CoopSession.LocalPlayerId;
            }

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                if (body.Value == null || body.Value.Body == null) continue;
                if (!Contains(body.Value.Body.transform.position)) continue;

                inside++;

                // The first one found is the one called. A double knockdown inside a quarter second is a
                // draw nobody can arbitrate from flags alone, and calling one of them is better than
                // calling neither.
                if (body.Value.IsDown && !down.IsAssigned) down = body.Key;
            }

            // Nobody is down, or the one who is has already had a round called against them, or there was
            // never a fight: a single player standing in a circle has not lost anything.
            if (!down.IsAssigned || inside < 2)
            {
                if (!down.IsAssigned) _calledAgainst = PlayerId.None;
                return;
            }

            if (down == _calledAgainst) return;

            _calledAgainst = down;

            ArenaRoundMessage round = new()
            {
                Round = _round == 0 ? (ushort)1 : _round,
                Loser = down,
                Fighters = (byte)Math.Min(inside, byte.MaxValue),
            };

            CoopSession.ReportArenaRound(round);
            TakeRound(round);
        }

        private static bool Contains(Vector3 position)
        {
            float dx = position.x - _center.x;
            float dy = position.y - _center.y;

            return (dx * dx) + (dy * dy) <= Radius * Radius;
        }

        private static void Broadcast()
        {
            CoopSession.ReportArenaState(Describe());
        }

        private static ArenaStateMessage Describe()
        {
            return new ArenaStateMessage
            {
                Active = Active ? (byte)1 : (byte)0,
                CenterX = _center.x,
                CenterY = _center.y,
                Radius = Active ? Radius : ArenaStateMessage.MinRadius,
            };
        }
    }
}
