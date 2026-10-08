using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.UI;
using Il2CppDrova;
using Il2CppDrova.InventorySystem;
using Il2CppDrova.Items;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.GameArena
{
    /// <summary>
    /// A host-marked circle where players may hurt each other whatever the session's friendly fire rule says.
    /// The host calls each round from the downed flags it is already sent.
    /// </summary>
    internal sealed class Arena : ICoopSystem
    {
        private const float ArbitrateSeconds = 0.25f;

        /// <summary>How long nobody in the circle can be hurt after a round, so the next one starts on a shared word.</summary>
        private const float BreatherSeconds = 3f;

        /// <summary>Rounds won per side (a team, or a player without one).</summary>
        private static readonly Dictionary<string, int> Wins = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal);

        /// <summary>Who fights for which team. Missing or 0 is a side of their own.</summary>
        private static readonly Dictionary<PlayerId, byte> Teams = new();

        // Side to a standing member, or None while all of it is down.
        private static readonly Dictionary<int, PlayerId> Sides = new();

        private static Vector2 _center;
        private static ushort _round;
        private static float _sinceArbitrate;
        private static float _breatherLeft;
        private static GameObject? _ring;
        private static Sprite? _ringDot;

        // Consumables carried on stepping into the circle. Null while outside it.
        private static Dictionary<IntPtr, (Item Item, int Amount)>? _stock;

        /// <summary>Whether an arena is marked.</summary>
        internal static bool Active { get; private set; }

        /// <summary>Meaningless while <see cref="Active"/> is false.</summary>
        internal static Vector2 Center => _center;

        /// <summary>The circle's radius in world units.</summary>
        internal static float Radius { get; private set; }

        /// <summary>Which round the arena is on.</summary>
        internal static ushort Round => _round;

        /// <summary>How many round results this machine has been told.</summary>
        internal static int Rounds { get; private set; }

        /// <summary>How many items the last refund gave back. For tests.</summary>
        internal static int Refunded { get; private set; }

        /// <summary>Takes the arena down and forgets it.</summary>
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
            Wins.Clear();
            Teams.Clear();
            DrawRing();
        }

        /// <summary>Whether the local player and <paramref name="other"/> may hurt each other right now.</summary>
        internal static bool AllowsHarm(PlayerId other)
        {
            if (CoopSession.FriendlyFire) return true;
            if (!Active || _breatherLeft > 0f) return false;

            return HoldsLocal() && Holds(other) && !SameSide(CoopSession.LocalPlayerId, other);
        }

        /// <summary>Whether two players, neither necessarily local, may hurt each other. Asked by the host for a relayed blow.</summary>
        internal static bool AllowsHarm(PlayerId one, PlayerId other)
        {
            if (CoopSession.FriendlyFire) return true;
            if (!Active || _breatherLeft > 0f) return false;

            return Holds(one) && Holds(other) && !SameSide(one, other);
        }

        /// <summary>Whether a player's body is inside the circle. False when the body is not here.</summary>
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

        /// <summary>Marks the arena where the host is standing. Host only.</summary>
        /// <returns>What to tell the player, whether or not it worked.</returns>
        internal static string Mark(float radius)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return "only the host can mark an arena.";
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return "no player to mark it around.";

            return MarkAt(player.transform.position, radius);
        }

        /// <summary>Marks the arena around a point. Host only. A radius of 0 or less means the largest ring.</summary>
        internal static string MarkAt(Vector2 where, float radius)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return "only the host can mark an arena.";

            if (radius <= 0f) radius = ArenaStateMessage.MaxRadius;
            float clamped = Mathf.Clamp(radius, ArenaStateMessage.MinRadius, ArenaStateMessage.MaxRadius);

            _center = where;
            Radius = clamped;
            Active = true;
            _round = 1;
            Wins.Clear();

            Broadcast();
            DrawRing();
            Overlay.Show("Arena marked - fight inside the ring!");

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
            Overlay.Show("The arena is closed. " + DescribeScore());

            return "arena taken down; the session's own friendly fire rule is what counts again.";
        }

        /// <summary>Sends the arena to one joining player. Host only.</summary>
        internal static void SendTo(INetPeer peer)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            SendArenaState(peer, Describe());

            foreach ((PlayerId player, byte team) in Teams)
            {
                SendArenaTeam(peer, new ArenaTeamMessage { Player = player, Team = team });
            }
        }

        /// <summary>Takes the host's word for where the arena is.</summary>
        private static void Take(in ArenaStateMessage message)
        {
            if (!message.IsPlausible()) return;

            bool wasActive = Active;

            Active = message.Active != 0;
            _center = new Vector2(message.CenterX, message.CenterY);
            Radius = message.Radius;

            DrawRing();

            // A re-mark resets the host's round and score too.
            if (Active)
            {
                Wins.Clear();
                _round = 1;
            }

            if (Active == wasActive) return;

            Overlay.Show(Active ? "Arena marked - fight inside the ring!" : "The arena is closed. " + DescribeScore());

            MelonLogger.Msg(Active
                ? $"[Arena] an arena is marked, {Radius:0.#} units across. Friendly fire is on inside it."
                : "[Arena] the arena is gone; the session's own friendly fire rule is what counts again.");
        }

        /// <summary>Takes the host's word on how a round ended.</summary>
        private static void TakeRound(in ArenaRoundMessage message)
        {
            if (!message.IsPlausible()) return;

            Rounds++;
            _round = (ushort)(message.Round + 1);

            string winner = message.Winner.IsAssigned ? SideName(message.Winner) : "nobody";
            if (message.Winner.IsAssigned)
            {
                string key = SideKey(message.Winner);
                Wins[key] = Wins.GetValueOrDefault(key) + 1;
                Labels[key] = winner;
            }

            MelonLogger.Msg($"[Arena] round {message.Round}: {winner} won, {message.Fighters} in the circle.");

            // Each machine stands its own fighter up at full health for the next round.
            if (HoldsLocal() && PlayerAccess.TryGetPlayer(out Actor player))
            {
                DownedState.StandUpNow();
                Health? health = player.GetHealth();
                if (health != null && health.CurHealth < health.MaxHealth) health.SetHealth(health.MaxHealth, null);
            }

            _breatherLeft = BreatherSeconds;
            Overlay.Show(message.Winner.IsAssigned
                ? $"Round {message.Round} goes to {winner}. Next round in {BreatherSeconds:0}..."
                : $"Round {message.Round}: everyone went down - a draw. Next round in {BreatherSeconds:0}...");
        }

        /// <summary>Rounds won per side, for coop_status and the closing word.</summary>
        internal static string DescribeScore()
        {
            if (Wins.Count == 0) return "no rounds won yet";

            return "rounds won: " + string.Join(", ", Wins.Select(entry => $"{Labels.GetValueOrDefault(entry.Key, entry.Key)} {entry.Value}"));
        }

        /// <summary>Whether the downed local player must stay down because another side still stands in the round.</summary>
        internal static bool KeepsDown()
        {
            if (!Active || _breatherLeft > 0f || !HoldsLocal()) return false;

            foreach ((PlayerId id, RemotePlayer body) in RemotePlayers.Bodies)
            {
                if (body.Body == null || body.IsDown) continue;
                if (!SameSide(CoopSession.LocalPlayerId, id) && Contains(body.Body.transform.position)) return true;
            }

            return false;
        }

        /// <summary>A player's arena team, 0 for none.</summary>
        internal static byte TeamOf(PlayerId player) => Teams.GetValueOrDefault(player);

        /// <summary>Whether two players share a team. Team 0 (none) never matches.</summary>
        private static bool SameSide(PlayerId one, PlayerId other)
        {
            byte team = TeamOf(one);
            return team != 0 && team == TeamOf(other);
        }

        // By id, never by name: two copies of one save carry the same character name.
        private static string SideKey(PlayerId player)
        {
            byte team = TeamOf(player);
            return team != 0 ? $"team:{team}" : $"player:{player.Value}";
        }

        private static string SideName(PlayerId player)
        {
            byte team = TeamOf(player);
            if (team == 0) return CoopSession.NameOf(player);

            return $"Team {team} ({string.Join(", ", Teams.Where(entry => entry.Value == team).Select(entry => CoopSession.NameOf(entry.Key)))})";
        }

        /// <summary>Puts a player in a team, 0 for none. A client asks the host.</summary>
        internal static void SetTeam(PlayerId player, byte team)
        {
            ArenaTeamMessage message = new() { Player = player, Team = team };
            if (CoopSession.State == CoopSessionState.Hosting) TakeTeam(message);
            else ReportArenaTeam(message);
        }

        /// <summary>The invitee joins the inviter's team, which is created if needed. Host only.</summary>
        internal static void JoinTeamOf(PlayerId invitee, PlayerId inviter)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            byte team = TeamOf(inviter);
            if (team == 0)
            {
                team = 1;
                while (Teams.ContainsValue(team) && team < 250) team++;
                SetTeam(inviter, team);
            }

            SetTeam(invitee, team);
        }

        /// <summary>Applies a team assignment. The host also broadcasts it.</summary>
        private static void TakeTeam(in ArenaTeamMessage message)
        {
            if (!message.Player.IsAssigned) return;

            if (message.Team == 0) Teams.Remove(message.Player);
            else Teams[message.Player] = message.Team;

            if (CoopSession.State == CoopSessionState.Hosting) ReportArenaTeam(message);

            string who = message.Player == CoopSession.LocalPlayerId ? "You" : CoopSession.NameOf(message.Player);
            Overlay.Show(message.Team == 0 ? $"{who} fight for no team." : $"{who} fight for Team {message.Team}.");
        }

        /// <summary>Counts down the breather on every machine.</summary>
        private static void Tick(float unscaledDeltaTime)
        {
            // A scene load destroys the ring.
            if (Active && _ring == null) DrawRing();

            KeepStock();

            if (_breatherLeft <= 0f) return;

            _breatherLeft -= unscaledDeltaTime;
            if (_breatherLeft <= 0f && Active) Overlay.Show($"Round {_round} - fight!");
        }

        /// <summary>Consumables and ammunition used inside the circle are given back on leaving it.</summary>
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

            Inventory? inventory = player.GetInventory();
            if (inventory == null) return;

            try
            {
                Dictionary<IntPtr, (Item Item, int Amount)> now = Consumables();
                int given = 0;
                foreach (KeyValuePair<IntPtr, (Item Item, int Amount)> had in before)
                {
                    int missing = had.Value.Amount - (now.TryGetValue(had.Key, out (Item Item, int Amount) left) ? left.Amount : 0);
                    if (missing <= 0) continue;

                    inventory.AddItem(new ItemStack(had.Value.Item, missing));
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

            Inventory? inventory = player.GetInventory();
            if (inventory == null) return counts;

            foreach (ItemStack stack in inventory.Cast<Il2CppDrova.InventorySystem.IInventoryContainer>().InventoryItems)
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

        private static void DrawRing()
        {
            try
            {
                if (_ring != null) UnityEngine.Object.Destroy(_ring);
                _ring = null;
                if (!Active) return;

                _ring = new GameObject("DrovaCoop.ArenaRing");

                // Sprite dots, not a line: this build ships no IL2CPP LineRenderer.
                if (_ringDot == null)
                {
                    Texture2D texture = new(4, 4);
                    Color[] pixels = new Color[16];
                    for (int index = 0; index < pixels.Length; index++) pixels[index] = Color.white;
                    texture.SetPixels(pixels);
                    texture.Apply();
                    _ringDot = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 1f);
                }

                // The player's sorting layer, since the default layer lies under the ground.
                Component? found = PlayerAccess.TryGetPlayer(out Actor player)
                    ? player.GetComponentInChildren(Il2CppInterop.Runtime.Il2CppType.Of<SpriteRenderer>())
                    : null;
                SpriteRenderer? reference = found == null ? null : found.TryCast<SpriteRenderer>();

                int dots = Mathf.Clamp(Mathf.RoundToInt(2f * Mathf.PI * Radius / 30f), 24, 256);
                for (int index = 0; index < dots; index++)
                {
                    float angle = index * Mathf.PI * 2f / dots;
                    GameObject dot = new("Dot");
                    dot.transform.SetParent(_ring.transform, false);
                    dot.transform.position = new Vector3(_center.x + Mathf.Cos(angle) * Radius, _center.y + Mathf.Sin(angle) * Radius, 0f);

                    Component? added = dot.AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<SpriteRenderer>());
                    SpriteRenderer? sprite = added == null ? null : added.TryCast<SpriteRenderer>();
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

        /// <summary>Host only: calls a round when somebody in the circle goes down.</summary>
        private static void CallRounds(float unscaledDeltaTime)
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
            if (_breatherLeft > 0f) return;

            Sides.Clear();
            int inside = 0;
            bool anyDown = false;

            if (HoldsLocal()) Count(CoopSession.LocalPlayerId, DownedState.IsDown, ref inside, ref anyDown);

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                if (body.Value == null || body.Value.Body == null) continue;
                if (!Contains(body.Value.Body.transform.position)) continue;

                Count(body.Key, body.Value.IsDown, ref inside, ref anyDown);
            }

            if (Sides.Count < 2 || !anyDown) return;

            int standingSides = 0;
            PlayerId winner = PlayerId.None;
            foreach (PlayerId standing in Sides.Values)
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

            ReportArenaRound(round);
            TakeRound(round);
        }

        private static void Count(PlayerId id, bool down, ref int inside, ref bool anyDown)
        {
            inside++;
            anyDown |= down;

            byte team = TeamOf(id);
            int side = team != 0 ? 1000 + team : id.Value;
            if (!Sides.TryGetValue(side, out PlayerId standing) || !standing.IsAssigned) Sides[side] = down ? PlayerId.None : id;
        }

        private static bool Contains(Vector3 position)
        {
            float dx = position.x - _center.x;
            float dy = position.y - _center.y;

            return (dx * dx) + (dy * dy) <= Radius * Radius;
        }

        private static void Broadcast()
        {
            ReportArenaState(Describe());
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

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<ArenaStateMessage>(MessageIds.ArenaState, OnArenaState);
            CoopSession.Register<ArenaRoundMessage>(MessageIds.ArenaRound, OnArenaRound);
            CoopSession.Register<ArenaTeamMessage>(MessageIds.ArenaTeam, (_, m) =>
            {
                if (CoopSession.State == CoopSessionState.Hosting || CoopSession.IsFromHost()) TakeTeam(m);
            });
        }

        /// <summary>Host to everyone: where the arena is, or that there is none.</summary>
        private static void ReportArenaState(in ArenaStateMessage message)
            => CoopSession.SendIfHost(message);

        /// <summary>Host to one joining player: the arena as it stands.</summary>
        private static void SendArenaState(INetPeer peer, in ArenaStateMessage message)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            NetworkAccess.Send(peer, message);
        }

        /// <summary>Host to everyone: how a round ended.</summary>
        private static void ReportArenaRound(in ArenaRoundMessage message)
            => CoopSession.SendIfHost(message);

        /// <summary>An arena team assignment: host to everyone, or a client's request to the host.</summary>
        private static void ReportArenaTeam(in ArenaTeamMessage message)
            => CoopSession.SendIfLive(message);

        /// <summary>Host to one joining player: a team as it stands.</summary>
        private static void SendArenaTeam(INetPeer peer, in ArenaTeamMessage message)
        {
            if (CoopSession.State == CoopSessionState.Hosting) NetworkAccess.Send(peer, message);
        }

        private static void OnArenaState(INetPeer peer, ArenaStateMessage message)
        {
            // The arena is the host's to mark.
            if (!CoopSession.IsFromHost()) return;

            Take(message);
        }

        private static void OnArenaRound(INetPeer peer, ArenaRoundMessage message)
        {
            if (!CoopSession.IsFromHost()) return;

            TakeRound(message);
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            CallRounds(unscaledDeltaTime);
            Tick(unscaledDeltaTime);
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            // Ids are reused: the next joiner must not inherit a departed player's arena team.
            if (CoopSession.State == CoopSessionState.Hosting) SetTeam(player, 0);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Reset();
    }
}
