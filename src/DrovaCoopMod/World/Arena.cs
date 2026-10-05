using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Items;
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


        private static float _sinceArbitrate;

        /// <summary>
        /// How long nobody in the circle can be hurt after a round. Everybody is standing and whole again by then, and
        /// the next round starts on a word both screens show rather than on whoever swings first.
        /// </summary>
        private const float BreatherSeconds = 3f;

        private static float _breatherLeft;

        /// <summary>Rounds won per side (a team, or a player without one), as this machine was told.</summary>
        private static readonly Dictionary<string, int> _wins = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);

        /// <summary>Who fights for which team. Missing or 0: a side of their own. The host's word, kept for the session.</summary>
        private static readonly Dictionary<PlayerId, byte> _teams = new();

        private static GameObject? _ring;
        private static Sprite? _ringDot;

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
            Refund();
            Active = false;
            _center = Vector2.zero;
            Radius = 0f;
            _round = 0;
            Rounds = 0;
            _sinceArbitrate = 0f;
            _breatherLeft = 0f;
            _wins.Clear();
            _teams.Clear();
            DrawRing();
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
            if (!Active || _breatherLeft > 0f) return false;

            return HoldsLocal() && Holds(other) && !SameSide(CoopSession.LocalPlayerId, other);
        }

        /// <summary>
        /// Whether two players may hurt each other, neither of them necessarily the one playing here - what
        /// the host asks about a blow it only relays. Asked as the host-and-attacker pair instead, a duel
        /// between two clients in the arena was refused whenever the host stood outside it.
        /// </summary>
        internal static bool AllowsHarm(PlayerId one, PlayerId other)
        {
            if (CoopSession.FriendlyFire) return true;
            if (!Active || _breatherLeft > 0f) return false;

            return Holds(one) && Holds(other) && !SameSide(one, other);
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

            return MarkAt(player.transform.position, radius);
        }

        /// <summary>
        /// Marks the arena around a point. Host only. A radius of 0 or less is the default, the largest ring there is: a
        /// fixed size in world units, the same for everyone. It used to be one of the host's screens, which was far too
        /// tight to fight in and grew or shrank with the host's resolution.
        /// </summary>
        internal static string MarkAt(Vector2 where, float radius)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return "only the host can mark an arena.";

            if (radius <= 0f) radius = ArenaStateMessage.MaxRadius;
            float clamped = Mathf.Clamp(radius, ArenaStateMessage.MinRadius, ArenaStateMessage.MaxRadius);

            _center = where;
            Radius = clamped;
            Active = true;
            _round = 1;
            _wins.Clear();

            Broadcast();
            DrawRing();
            Announce("Arena marked - fight inside the ring!");

            return $"arena marked here, radius {clamped:0.#} units. Friendly fire is on inside it " +
                "whatever the session rule says, and off again outside.";
        }

        /// <summary>Takes the arena down. Host only.</summary>
        internal static string Clear()
        {
            if (CoopSession.State != CoopSessionState.Hosting) return "only the host can take the arena down.";
            if (!Active) return "there is no arena.";

            Active = false;
            _round = 0;
            Broadcast();
            DrawRing();
            Announce("The arena is closed. " + DescribeScore());

            return "arena taken down; the session's own friendly fire rule is what counts again.";
        }

        /// <summary>Sends the arena to one joining player. Host only.</summary>
        internal static void SendTo(Drova_Modding_API.Systems.Networking.INetPeer peer)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            CoopSession.SendArenaState(peer, Describe());

            foreach ((PlayerId player, byte team) in _teams)
            {
                CoopSession.SendArenaTeam(peer, new ArenaTeamMessage { Player = player, Team = team });
            }
        }

        /// <summary>Takes the host's word for where the arena is.</summary>
        internal static void Take(in ArenaStateMessage message)
        {
            if (!message.IsPlausible()) return;

            bool wasActive = Active;

            Active = message.Active != 0;
            _center = new Vector2(message.CenterX, message.CenterY);
            Radius = message.Radius;

            DrawRing();

            // Every "marked" from the host is a fresh mark (a re-mark resets the host's round and score too).
            if (Active)
            {
                _wins.Clear();
                _round = 1;
            }

            if (Active == wasActive) return;

            Announce(Active ? "Arena marked - fight inside the ring!" : "The arena is closed. " + DescribeScore());

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

            string winner = message.Winner.IsAssigned ? SideName(message.Winner) : "nobody";
            if (message.Winner.IsAssigned)
            {
                string key = SideKey(message.Winner);
                _wins[key] = _wins.GetValueOrDefault(key) + 1;
                _labels[key] = winner;
            }

            MelonLogger.Msg($"[Arena] round {message.Round}: {winner} won, {message.Fighters} in the circle.");

            // Everybody in the circle starts the next round whole: the loser up at once instead of after the downed
            // clock, and every fighter on full health. Each machine does it for its own player.
            if (HoldsLocal() && PlayerAccess.TryGetPlayer(out Actor player))
            {
                DownedState.StandUpNow();
                Health health = player.GetHealth();
                if (health != null && health.CurHealth < health.MaxHealth) health.SetHealth(health.MaxHealth, null);
            }

            _breatherLeft = BreatherSeconds;
            Announce(message.Winner.IsAssigned
                ? $"Round {message.Round} goes to {winner}. Next round in {BreatherSeconds:0}..."
                : $"Round {message.Round}: everyone went down - a draw. Next round in {BreatherSeconds:0}...");
        }

        /// <summary>Rounds won per side, for coop_status and the closing word.</summary>
        internal static string DescribeScore()
        {
            if (_wins.Count == 0) return "no rounds won yet";

            return "rounds won: " + string.Join(", ", _wins.Select(entry => $"{_labels.GetValueOrDefault(entry.Key, entry.Key)} {entry.Value}"));
        }

        /// <summary>
        /// Whether the local player, down in the circle, has to stay down: while a round is on and somebody of another
        /// side still stands, getting up on the clock would make a team fight endless. The round's end stands them up.
        /// </summary>
        internal static bool KeepsDown()
        {
            if (!Active || _breatherLeft > 0f || !HoldsLocal()) return false;

            foreach ((PlayerId id, RemotePlayer body) in RemotePlayers.Bodies)
            {
                if (body?.Body == null || body.IsDown) continue;
                if (!SameSide(CoopSession.LocalPlayerId, id) && Contains(body.Body.transform.position)) return true;
            }

            return false;
        }

        /// <summary>A player's arena team, 0 for none.</summary>
        internal static byte TeamOf(PlayerId player) => _teams.GetValueOrDefault(player);

        /// <summary>Whether two players fight on the same side: the same team, which 0 (none) never is.</summary>
        internal static bool SameSide(PlayerId one, PlayerId other)
        {
            byte team = TeamOf(one);
            return team != 0 && team == TeamOf(other);
        }

        // By id, never by name: two players can carry the same character name (two copies of one save do), and then
        // they were one side and no round was ever called.
        private static string SideKey(PlayerId player)
        {
            byte team = TeamOf(player);
            return team != 0 ? $"team:{team}" : $"player:{player.Value}";
        }

        private static string SideName(PlayerId player)
        {
            byte team = TeamOf(player);
            if (team == 0) return NameOf(player);

            return $"Team {team} ({string.Join(", ", _teams.Where(entry => entry.Value == team).Select(entry => NameOf(entry.Key)))})";
        }

        /// <summary>
        /// Puts a player in a team, 0 for none. Host: decides and tells everyone. Client: asks the host.
        /// </summary>
        internal static void SetTeam(PlayerId player, byte team)
        {
            ArenaTeamMessage message = new() { Player = player, Team = team };
            if (CoopSession.State == CoopSessionState.Hosting) TakeTeam(message, fromHost: true);
            else CoopSession.ReportArenaTeam(message);
        }

        /// <summary>The invitee joins the inviter's team; an inviter without one starts a new one. Host only.</summary>
        internal static void JoinTeamOf(PlayerId invitee, PlayerId inviter)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            byte team = TeamOf(inviter);
            if (team == 0)
            {
                team = 1;
                while (_teams.ContainsValue(team) && team < 250) team++;
                SetTeam(inviter, team);
            }

            SetTeam(invitee, team);
        }

        /// <summary>A team assignment: on the host, a client's request (or its own), broadcast; elsewhere, the host's word.</summary>
        internal static void TakeTeam(in ArenaTeamMessage message, bool fromHost)
        {
            if (!message.Player.IsAssigned) return;

            if (message.Team == 0) _teams.Remove(message.Player);
            else _teams[message.Player] = message.Team;

            if (CoopSession.State == CoopSessionState.Hosting) CoopSession.ReportArenaTeam(message);

            string who = message.Player == CoopSession.LocalPlayerId ? "You" : NameOf(message.Player);
            Announce(message.Team == 0 ? $"{who} fight for no team." : $"{who} fight for Team {message.Team}.");
        }


        /// <summary>Counts down the breather on every machine; the round-calling below is the host's.</summary>
        internal static void Tick(float unscaledDeltaTime)
        {
            // A load takes the ring with the rest of the scene while the arena stands.
            if (Active && _ring == null) DrawRing();

            KeepStock();

            if (_breatherLeft <= 0f) return;

            _breatherLeft -= unscaledDeltaTime;
            if (_breatherLeft <= 0f && Active) Announce($"Round {_round} - fight!");
        }

        // What this player carried of each consumable on stepping into the circle; null while outside it.
        private static Dictionary<IntPtr, (Item Item, int Amount)>? _stock;

        /// <summary>How many items the last refund gave back. For tests.</summary>
        internal static int Refunded { get; private set; }

        /// <summary>
        /// A fight in the circle costs nothing: what this player drank, ate, threw or cast in it comes back when they
        /// leave it or the arena closes - a sparring match, not a drain on the bag. Counted on stepping in, and only
        /// what went down is topped up: a pickup inside is kept, and nothing that merely moved to an equipment slot
        /// counts, because only consumables and ammunition are looked at.
        /// </summary>
        private static void KeepStock()
        {
            bool inside = HoldsLocal();
            if (inside && _stock == null) _stock = Consumables();
            else if (!inside && _stock != null) Refund();
        }

        private static void Refund()
        {
            if (_stock == null) return;

            Dictionary<IntPtr, (Item Item, int Amount)> before = _stock;
            _stock = null;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            try
            {
                Dictionary<IntPtr, (Item Item, int Amount)> now = Consumables();
                int given = 0;
                foreach (KeyValuePair<IntPtr, (Item Item, int Amount)> had in before)
                {
                    int missing = had.Value.Amount - (now.TryGetValue(had.Key, out (Item Item, int Amount) left) ? left.Amount : 0);
                    if (missing <= 0) continue;

                    player.GetInventory().AddItem(new ItemStack(had.Value.Item, missing));
                    given += missing;
                }

                Refunded = given;
                if (given > 0) MelonLogger.Msg($"[Arena] {given} consumables used in the fight were given back.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] giving back what the fight used failed: " + e);
            }
        }

        private static Dictionary<IntPtr, (Item Item, int Amount)> Consumables()
        {
            Dictionary<IntPtr, (Item Item, int Amount)> counts = new();
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return counts;

            foreach (ItemStack stack in player.GetInventory().Cast<Il2CppDrova.InventorySystem.IInventoryContainer>().InventoryItems)
            {
                Item? item = stack?.Item;
                if (item == null || stack!.Amount <= 0 || item.Category == null) continue;

                ItemSubCategory sub = item.Category.SubCategory;
                if (item.Category.MainCategory != ItemMainCategory.Consumable
                    && sub != ItemSubCategory.Weapon_Bow_Ammo && sub != ItemSubCategory.Weapon_Slingshot_Ammo) continue;

                counts[item.Pointer] = (item, (counts.TryGetValue(item.Pointer, out (Item Item, int Amount) had) ? had.Amount : 0) + stack.Amount);
            }

            return counts;
        }

        private static string NameOf(PlayerId player)
        {
            return CoopSession.Players.TryGetValue(player, out string? name) && !string.IsNullOrEmpty(name) ? name : $"player {player.Value}";
        }

        /// <summary>Big text in the middle of the screen, the game's own location-name overlay.</summary>
        private static void Announce(string text)
        {
            try
            {
                if (GUIGameHandler.TryGet(out GUIGameHandler gui) && gui != null) gui.GetOverlayTextHandler()?.CreateOverlayText(text);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Arena] showing '" + text + "' failed: " + e.Message);
            }
        }

        /// <summary>
        /// The circle drawn on the ground, so both players can see where the rule changes. Rebuilt whenever the arena
        /// changes; gone when it is.
        /// </summary>
        private static void DrawRing()
        {
            try
            {
                if (_ring != null) UnityEngine.Object.Destroy(_ring);
                _ring = null;
                if (!Active) return;

                _ring = new GameObject("DrovaCoop.ArenaRing");

                // Dots, not a line: this build ships no LineRenderer ("does not have a corresponding IL2CPP class
                // pointer"), so no ring was ever drawn. A sprite renderer is what the game draws everything with.
                if (_ringDot == null)
                {
                    Texture2D texture = new(4, 4);
                    Color[] pixels = new Color[16];
                    for (int index = 0; index < pixels.Length; index++) pixels[index] = Color.white;
                    texture.SetPixels(pixels);
                    texture.Apply();
                    _ringDot = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 1f);
                }

                // Drawn on the layer the player's own sprite is, just above it: the default layer lies under the ground.
                SpriteRenderer? reference = Drova_Modding_API.Access.PlayerAccess.TryGetPlayer(out Actor player)
                    ? player.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<SpriteRenderer>())?.TryCast<SpriteRenderer>()
                    : null;

                // A dot every 30 units of the circle.
                int dots = Mathf.Clamp(Mathf.RoundToInt(2f * Mathf.PI * Radius / 30f), 24, 256);
                for (int index = 0; index < dots; index++)
                {
                    float angle = index * Mathf.PI * 2f / dots;
                    GameObject dot = new("Dot");
                    dot.transform.SetParent(_ring.transform, false);
                    dot.transform.position = new Vector3(_center.x + Mathf.Cos(angle) * Radius, _center.y + Mathf.Sin(angle) * Radius, 0f);

                    SpriteRenderer? sprite = dot.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<SpriteRenderer>())?.TryCast<SpriteRenderer>();
                    if (sprite == null) return;
                    sprite.sprite = _ringDot;
                    sprite.color = new Color(1f, 0.35f, 0.2f, 0.8f);
                    if (reference != null) sprite.sortingLayerID = reference.sortingLayerID;
                    sprite.sortingOrder = 32000;
                    dot.transform.localScale = new Vector3(3f, 3f, 1f);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Arena] drawing the ring failed: " + e.Message);
            }
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
            // The breather is the round's end still being carried out; everybody is getting up.
            if (_breatherLeft > 0f) return;

            // Every side in the circle - a team, or a player alone (by id) - and one member of it still standing, if any.
            // A reused table: this runs four times a second for as long as an arena stands.
            _sides.Clear();
            int inside = 0;
            bool anyDown = false;

            if (HoldsLocal()) Count(CoopSession.LocalPlayerId, DownedState.IsDown, ref inside, ref anyDown);

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                if (body.Value == null || body.Value.Body == null) continue;
                if (!Contains(body.Value.Body.transform.position)) continue;

                Count(body.Key, body.Value.IsDown, ref inside, ref anyDown);
            }

            // A fight needs two sides in the circle and somebody down; it is over when at most one side still stands.
            if (_sides.Count < 2 || !anyDown) return;

            int standingSides = 0;
            PlayerId winner = PlayerId.None;
            foreach (PlayerId standing in _sides.Values)
            {
                if (!standing.IsAssigned) continue;
                standingSides++;
                winner = standing;
            }

            if (standingSides > 1) return;

            ArenaRoundMessage round = new()
            {
                Round = _round == 0 ? (ushort)1 : _round,
                Winner = winner,
                Fighters = (byte)Math.Min(inside, byte.MaxValue),
            };

            CoopSession.ReportArenaRound(round);
            TakeRound(round);
        }

        // Side -> a standing member, or None while all of it is down.
        private static readonly Dictionary<int, PlayerId> _sides = new();

        private static void Count(PlayerId id, bool down, ref int inside, ref bool anyDown)
        {
            inside++;
            anyDown |= down;

            byte team = TeamOf(id);
            int side = team != 0 ? 1000 + team : id.Value;
            if (!_sides.TryGetValue(side, out PlayerId standing) || !standing.IsAssigned) _sides[side] = down ? PlayerId.None : id;
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
