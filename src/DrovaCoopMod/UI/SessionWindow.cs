using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.Session;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.UI
{
    /// <summary>
    /// The session window: host, join, players, arena, rules and save backups. IMGUI, because the game's
    /// options-menu builder has no text field. Gameplay input is off while it is open.
    /// </summary>
    internal static class SessionWindow
    {
        private const float Margin = 16f;
        private const float Width = 480f;
        private const float Padding = 16f;
        private const float RowHeight = 26f;
        private const float HintHeight = 18f;
        private const float RowGap = 8f;
        private const float SectionGap = 16f;
        private const float ButtonWidth = 120f;
        private const float SmallButtonWidth = 72f;
        private const float LabelWidth = 150f;
        private const float ValueWidth = 72f;
        private const float BackupListHeight = 300f;
        private const int MaxArenaRounds = 20;

        private const int MaxCodeLength = 64;
        private const int MaxPasswordLength = 128;
        private const int MaxAddressLength = 64;

        // Drova's HUD: peat-dark panels in copper frames. Lichen marks what is chosen, rust what cannot be undone.
        private static readonly Color Panel = new(0.102f, 0.078f, 0.063f, 0.95f);
        private static readonly Color Frame = new(0.549f, 0.353f, 0.227f, 1f);
        private static readonly Color Bone = new(0.910f, 0.875f, 0.800f, 1f);
        private static readonly Color Ash = new(0.620f, 0.580f, 0.514f, 1f);
        private static readonly Color Lichen = new(0.561f, 0.639f, 0.353f, 1f);
        private static readonly Color Rust = new(0.659f, 0.263f, 0.184f, 1f);
        private static readonly Color ButtonColor = new(0.165f, 0.129f, 0.102f, 1f);
        private static readonly Color ButtonHover = new(0.227f, 0.176f, 0.133f, 1f);
        private static readonly Color FieldColor = new(0.059f, 0.047f, 0.035f, 1f);

        private static bool _seeded;
        private static string _code = string.Empty;
        private static string _ownCode = string.Empty;
        private static string _password = string.Empty;
        private static string _address = string.Empty;
        private static string _portText = string.Empty;
        private static string _addresses = string.Empty;
        private static bool _direct;
        private static bool _showRules;
        private static bool _showMore;
        private static bool _showAddress;
        private static int _arenaRadius = (int)ArenaStateMessage.DefaultRadius;
        private static int _arenaRounds;
        private static List<(DiscoveredHost Found, LanGameInfo Info)> _games = new();
        private static List<(SaveBackups.Slot Slot, string Hero, bool Played)> _backups = new();
        private static (SaveBackups.Slot Slot, string Hero, bool BeforeSession)? _confirm;
        private static Vector2 _backupScroll;
        private static string _lastLine = string.Empty;
        private static bool _drawFailed;
        private static bool _gameplayInputOff;

        private static View _view;
        private static Tab _tab;

        // Where the previous pass's rows ended, since the box is drawn before the rows it sits behind.
        private static float _height;

        private static Texture2D? _panelTexture;
        private static Texture2D? _frameTexture;
        private static Texture2D? _lichenTexture;
        private static GUIStyle? _title;
        private static GUIStyle? _label;
        private static GUIStyle? _hint;
        private static GUIStyle? _field;
        private static GUIStyle? _button;
        private static GUIStyle? _primary;
        private static GUIStyle? _danger;
        private static GUIStyle? _choice;
        private static GUIStyle? _tabStyle;
        private static GUIStyle? _link;
        private static GUIStyle? _toggle;

        private enum View : byte { Start, Host, Join, Backups }

        private enum Tab : byte { Players, Arena, Rules }

        internal static bool Visible { get; private set; }

        /// <summary>Whether drawing failed once, which keeps the window shut for the run.</summary>
        internal static bool DrawFailed => _drawFailed;

        /// <summary>Opens the window on a view (start, host, join, backups) or session tab (players, arena, rules), for tests.</summary>
        internal static string Open(string page)
        {
            if (Enum.TryParse(page, ignoreCase: true, out Tab tab)) _tab = tab;
            else if (Enum.TryParse(page, ignoreCase: true, out View view)) _view = view;
            else return "unknown page " + page;

            if (_view == View.Backups) LoadBackups();
            Show();
            return "open on " + page;
        }

        private static void Show()
        {
            if (!_seeded)
            {
                _seeded = true;
                _code = CoopPreferences.SessionCode.Trim();
                _password = CoopPreferences.SessionPassword;
                _portText = CoopPreferences.LanPort.ToString();
            }

            _addresses = CoopActions.AddressLine(CoopPreferences.LanPort);
            if (_view == View.Backups) LoadBackups();

            Visible = true;
        }

        private static void Hide()
        {
            Visible = false;
            _confirm = null;
            RestoreGameplayInput();
            CoopActions.Browse(false);

            // Here rather than on each change: a slider drag is many changes.
            CoopPreferences.Save();
        }

        /// <summary>Returns the line to tell the player.</summary>
        internal static string Toggle()
        {
            if (Visible)
            {
                Hide();
                return "session window closed.";
            }

            Show();
            return "session window open. Escape closes it.";
        }

        /// <summary>Draws the window. Call from the melon's <c>OnGUI</c> on every event, not only repaints.</summary>
        internal static void Draw()
        {
            if (!Visible || _drawFailed) return;

            try
            {
                Event current = Event.current;
                if (current != null && current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
                {
                    Hide();
                    current.Use();
                    return;
                }

                // Every repaint, because the game turns its maps back on when one of its menus closes.
                if (current != null && current.type == EventType.Repaint && Core.InGameplay)
                {
                    SuppressGameplayInput();
                }

                EnsureStyles();
                DrawContents();
            }
            catch (Exception e)
            {
                // Once per run, since OnGUI runs several times a frame.
                _drawFailed = true;
                Hide();
                MelonLogger.Error("[Coop] the session window failed to draw and stays closed: " + e);
            }
        }

        private static void DrawContents()
        {
            CoopSessionState state = CoopSession.State;
            bool inSession = CoopSession.IsLive || state == CoopSessionState.Connecting;
            if (inSession && _view != View.Backups) _view = View.Start;

            // Discovery runs only while the join view is open. Layout comes before the repaint that draws the same rows.
            CoopActions.Browse(!inSession && _view == View.Join);
            if (Event.current != null && Event.current.type == EventType.Layout && NetworkAccess.IsDiscovering)
            {
                _games = CoopActions.FoundGames();
            }

            Rect box = new(Margin, Margin, Width, _height);
            GUI.DrawTexture(box, _frameTexture);
            GUI.DrawTexture(new Rect(box.x + 1f, box.y + 1f, box.width - 2f, box.height - 2f), _panelTexture);

            float x = box.x + Padding;
            float width = Width - (2f * Padding);
            float y = box.y + Padding;

            GUI.Label(Row(x, y, width), "Play together", _title);
            if (GUI.Button(new Rect(x + width - SmallButtonWidth, y, SmallButtonWidth, RowHeight), "Close", _button)) Hide();
            y += RowHeight + RowGap;

            string? refusal = VersionCheck.Refusal;
            if (refusal != null) y = Paragraph(x, y, width, refusal, _label);

            if (_view == View.Backups) y = DrawBackups(x, y, width);
            else if (inSession) y = DrawSession(x, y, width, state);
            else if (_view == View.Host) y = DrawHost(x, y, width);
            else if (_view == View.Join) y = DrawJoin(x, y, width);
            else y = DrawStart(x, y, width, state);

            if (_lastLine.Length > 0)
            {
                y += RowGap;
                y = Paragraph(x, y, width, _lastLine, _hint);
            }

            _height = y + Padding - box.y;
        }

        private static float DrawStart(float x, float y, float width, CoopSessionState state)
        {
            if (state == CoopSessionState.Failed)
            {
                y = Paragraph(x, y, width, "Could not connect: " + (CoopSession.LastError ?? "no reason given."), _label);
            }

            y = Choice(x, y, width, "Host a game", "Your friends join your world.", View.Host);
            y = Choice(x, y, width, "Join a game", "Play in a friend's world.", View.Join);
            return Choice(x, y, width, "Save backups", "Undo what a session changed in a save.", View.Backups);
        }

        private static float Choice(float x, float y, float width, string title, string line, View view)
        {
            const float height = 52f;
            if (GUI.Button(new Rect(x, y, width, height), string.Empty, _choice))
            {
                _view = view;
                _lastLine = string.Empty;
                if (view == View.Backups) LoadBackups();
            }

            GUI.Label(new Rect(x + 12f, y + 6f, width - 24f, RowHeight - 4f), title, _label);
            GUI.Label(new Rect(x + 12f, y + 28f, width - 24f, HintHeight), line, _hint);
            return y + height + RowGap;
        }

        private static float DrawHost(float x, float y, float width)
        {
            GUI.Label(Row(x, y, width), "Where do your friends play?", _label);
            y += RowHeight + RowGap;

            if (Radio(x, y, width, !_direct, "Over the internet", "They join with a code you send them.")) _direct = false;
            y += RowHeight + HintHeight + RowGap;
            if (Radio(x, y, width, _direct, "On the same network", "They find your game in their list.")) _direct = true;
            y += RowHeight + HintHeight + SectionGap;

            GUI.Label(Row(x, y, width), "What do you play?", _label);
            y += RowHeight + RowGap;

            if (Radio(x, y, width, !CoopPreferences.ArenaMode, "Together", "One world: quests, loot and enemies are shared.")) CoopPreferences.ArenaMode = false;
            y += RowHeight + HintHeight + RowGap;
            if (Radio(x, y, width, CoopPreferences.ArenaMode, "Arena only", "Everyone keeps their own world. Only fights against each other are shared.")) CoopPreferences.ArenaMode = true;
            y += RowHeight + HintHeight + SectionGap;

            if (!_direct)
            {
                y = Password(x, y, width, "Password (optional)");
                y = Hint(x, y - RowGap + 2f, width, "Keeps strangers out and encrypts the game.") + RowGap;
            }

            y = Foldout(x, y, width, ref _showRules, "Rules: " + RulesSummary(CoopPreferences.FriendlyFire, CoopPreferences.SharedLoot,
                CoopPreferences.EnemyHealthPercent, CoopPreferences.EnemyDamagePercent));
            if (_showRules) y = EditRules(x + 16f, y, width - 16f);

            y = Foldout(x, y, width, ref _showMore, "More options");
            if (_showMore)
            {
                if (_direct)
                {
                    y = Field(x + 16f, y, width - 16f, "UDP port", ref _portText, 5);
                    y = Hint(x + 16f, y - RowGap + 2f, width - 16f, "For friends over the internet, forward this port on your router.") + RowGap;
                }
                else
                {
                    y = Field(x + 16f, y, width - 16f, "Own code", ref _ownCode, MaxCodeLength);
                    y = Hint(x + 16f, y - RowGap + 2f, width - 16f, "Leave it empty and a code is picked for you.") + RowGap;
                }
            }

            y += RowGap;
            float buttonX = x;
            if (Action(ref buttonX, y, "Start hosting", _primary)) _lastLine = Host();
            if (Action(ref buttonX, y, "Back", _button)) _view = View.Start;
            return y + RowHeight;
        }

        private static float DrawJoin(float x, float y, float width)
        {
            GUI.Label(Row(x, y, width), "Games on your network", _label);
            y += RowHeight + 2f;

            if (_games.Count == 0)
            {
                y = Hint(x, y, width, "Looking for games... A friend hosting on the same network shows up here.") + RowGap;
            }

            foreach ((DiscoveredHost found, LanGameInfo info) in _games)
            {
                // Greyed rather than hidden, so a version mismatch explains itself.
                GUI.enabled = info.IsJoinable(CoopProtocol.Version);
                if (GUI.Button(Row(x, y, width), $"{info.HostName}   {CoopActions.Describe(info)}", _button))
                {
                    Remember();
                    _lastLine = CoopActions.JoinFound(found, info);
                }

                GUI.enabled = true;
                y += RowHeight + RowGap;
            }

            y += SectionGap - RowGap;
            GUI.Label(Row(x, y, width), "Join with a code", _label);
            y += RowHeight + 2f;

            float fieldWidth = width - SmallButtonWidth - RowGap;
            _code = GUI.TextField(Row(x, y, fieldWidth), _code, MaxCodeLength, _field);
            if (GUI.Button(new Rect(x + fieldWidth + RowGap, y, SmallButtonWidth, RowHeight), "Paste", _button)) _lastLine = PasteCode();
            y += RowHeight + RowGap;
            y = Password(x, y, width, "Password, if any");

            y = Foldout(x, y, width, ref _showAddress, "Join by address");
            if (_showAddress)
            {
                y = Field(x + 16f, y, width - 16f, "Address", ref _address, MaxAddressLength);
                y = Hint(x + 16f, y - RowGap + 2f, width - 16f, "The host's IP, or IP:port.") + RowGap;
            }

            y += RowGap;
            float buttonX = x;
            if (Action(ref buttonX, y, "Join", _primary)) _lastLine = Join();
            if (Action(ref buttonX, y, "Back", _button)) _view = View.Start;
            return y + RowHeight;
        }

        private static float DrawSession(float x, float y, float width, CoopSessionState state)
        {
            y = DrawConnection(x, y, width, state);
            if (state == CoopSessionState.Connecting)
            {
                float leaveX = x;
                if (Action(ref leaveX, y, "Cancel", _danger)) _lastLine = CoopActions.Leave();
                return y + RowHeight;
            }

            // Arena and Go to need a world to stand in.
            bool world = Core.InGameplay;
            if (!world && _tab == Tab.Arena) _tab = Tab.Players;

            float tabX = x;
            y += RowGap;
            TabButton(ref tabX, y, Tab.Players, "Players");
            if (world) TabButton(ref tabX, y, Tab.Arena, "Arena");
            TabButton(ref tabX, y, Tab.Rules, "Rules");
            y += RowHeight + 2f;
            GUI.DrawTexture(new Rect(x, y, width, 1f), _frameTexture);
            y += SectionGap;

            y = _tab switch
            {
                Tab.Arena => DrawArena(x, y, width),
                Tab.Rules => DrawRules(x, y, width),
                _ => DrawPlayers(x, y, width, world),
            };

            y += SectionGap;
            float buttonX = x;
            if (Action(ref buttonX, y, "Leave session", _danger)) _lastLine = CoopActions.Leave();
            return y + RowHeight;
        }

        private static float DrawConnection(float x, float y, float width, CoopSessionState state)
        {
            if (state == CoopSessionState.Connecting) return Paragraph(x, y, width, "Connecting...", _label);

            string? code = CoopSession.SessionCode;
            bool hosting = state == CoopSessionState.Hosting;

            if (hosting && code != null)
            {
                GUI.Label(Row(x, y, width), "Send your friends this code:", _label);
                y += RowHeight;
                GUI.Label(Row(x, y, width - SmallButtonWidth - RowGap), code, _title);
                if (GUI.Button(new Rect(x + width - SmallButtonWidth, y, SmallButtonWidth, RowHeight), "Copy", _button)) _lastLine = CopyCode();
                y += RowHeight;
                if (!CoopSession.IsEncrypted) y = Hint(x, y, width, "Not encrypted. Set a password next time to keep it private.");
                return y + RowGap;
            }

            if (hosting)
            {
                y = Paragraph(x, y, width, "Friends on your network find your game in their list.", _label);
                return Hint(x, y - RowGap, width, $"Your address: {_addresses}. Not encrypted.") + RowGap;
            }

            return Paragraph(x, y, width, $"You are in {CoopSession.NameOf(PlayerId.Host)}'s game.", _label);
        }

        private static float DrawPlayers(float x, float y, float width, bool world)
        {
            foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
            {
                bool self = player.Key == CoopSession.LocalPlayerId;
                GUI.Label(Row(x, y, LabelWidth + 40f), self ? player.Value + " (you)" : player.Value, _label);

                float? ping = self ? null : CoopSession.RoundTripMs(player.Key);
                if (ping.HasValue) GUI.Label(new Rect(x + LabelWidth + 48f, y, ValueWidth, RowHeight), $"{ping.Value:0} ms", _hint);

                if (!self && world && GUI.Button(new Rect(x + width - SmallButtonWidth, y, SmallButtonWidth, RowHeight), "Go to", _button))
                {
                    _lastLine = CoopActions.GoTo(player.Key);
                }

                // Here as well as in the talk menu, which the game refuses on an opponent in the ring.
                if (!self && world && GUI.Button(new Rect(x + width - (2f * SmallButtonWidth) - RowGap, y, SmallButtonWidth, RowHeight), "Trade", _button))
                {
                    _lastLine = Trade.OpenWith(player.Key);
                    Hide();
                }

                y += RowHeight + 4f;
            }

            if (!CoopSession.JoinedWorld) return y;

            y += RowGap;
            float buttonX = x;
            if (Action(ref buttonX, y, "Catch up", _button)) _lastLine = CoopActions.CatchUp(deep: false);
            return Hint(x, y + RowHeight + 2f, width, "If a quest or door looks different from the host's, fetch the host's state.");
        }

        private static float DrawArena(float x, float y, float width)
        {
            bool host = CoopSession.State == CoopSessionState.Hosting;

            if (!Arena.Active)
            {
                y = Paragraph(x, y, width, host
                    ? "Mark a ring where players can fight each other. The last side standing wins the round, then everyone gets up healed."
                    : "No ring yet. The host can mark one where players fight each other.", _label);
            }
            else if (!Arena.Fighting)
            {
                y = Paragraph(x, y, width, host
                    ? "The ring is marked. Pick sides, then start the fight."
                    : "The ring is marked. Pick your side; the host starts the fight.", _label);
            }
            else
            {
                string score = Arena.DescribeScore();
                string round = Arena.RoundLimit == 0 ? $"Round {Arena.Round}" : $"Round {Arena.Round} of {Arena.RoundLimit}";
                y = Paragraph(x, y, width, $"{round}. You are {(Arena.HoldsLocal() ? "inside" : "outside")} the ring. " +
                    char.ToUpperInvariant(score[0]) + score[1..] + ".", _label);
            }

            PlayerId local = CoopSession.LocalPlayerId;
            byte team = Arena.TeamOf(local);
            GUI.Label(Row(x, y, LabelWidth), "Your side", _label);
            float valueX = x + LabelWidth;
            if (GUI.Button(new Rect(valueX, y, 32f, RowHeight), "<", _button) && team > 0) Arena.SetTeam(local, (byte)(team - 1));
            GUI.Label(new Rect(valueX + 40f, y, 80f, RowHeight), team == 0 ? "Alone" : $"Team {team}", _label);
            if (GUI.Button(new Rect(valueX + 120f, y, 32f, RowHeight), ">", _button) && team < CoopProtocol.MaxClients + 1)
            {
                Arena.SetTeam(local, (byte)(team + 1));
            }

            y = Hint(x, y + RowHeight + 2f, width, "Players on the same team cannot hurt each other.") + SectionGap;

            if (!host) return y;

            float buttonX = x;
            if (Arena.Fighting)
            {
                if (Action(ref buttonX, y, "Stop fight", _danger)) _lastLine = Arena.Stop();
                return y + RowHeight;
            }

            if (Arena.Active)
            {
                _arenaRounds = Slider(x, y, width, "Rounds", _arenaRounds, 0, MaxArenaRounds, string.Empty, "No limit");
                y = Hint(x, y + RowHeight - 2f, width, "After the last round the fight stops and the score is read out.") + RowGap;
                if (Action(ref buttonX, y, "Start fight", _primary)) _lastLine = Arena.Start((byte)_arenaRounds);
                y += RowHeight + SectionGap;
                buttonX = x;
            }

            _arenaRadius = Slider(x, y, width, "Ring size", _arenaRadius, (int)ArenaStateMessage.MinRadius, (int)ArenaStateMessage.MaxRadius, string.Empty);
            y += RowHeight + RowGap;
            if (Action(ref buttonX, y, Arena.Active ? "Move ring here" : "Mark ring here", Arena.Active ? _button : _primary)) _lastLine = Arena.Mark(_arenaRadius);
            if (Arena.Active && Action(ref buttonX, y, "Remove ring", _danger)) _lastLine = Arena.Clear();
            return y + RowHeight;
        }

        private static float DrawRules(float x, float y, float width)
        {
            if (CoopSession.State == CoopSessionState.Hosting) return EditRules(x, y, width);

            y = RuleLine(x, y, "Mode", CoopSession.ArenaMode ? "Arena only" : "Together");
            y = RuleLine(x, y, "Friendly fire", CoopSession.FriendlyFire ? "On" : "Off");
            if (CoopSession.ArenaMode) return Hint(x, y + 2f, width, "Only the host can change these.");

            y = RuleLine(x, y, "Loot", CoopSession.LootMode == CoopProtocol.LootModeShared ? "Shared" : "Each their own");
            y = RuleLine(x, y, "Health after getting up", $"{CoopSession.RecoveryHealthPercent} %");
            y = RuleLine(x, y, "Enemy health", $"{CoopSession.EnemyHealthPercent} %");
            y = RuleLine(x, y, "Enemy damage", $"{CoopSession.EnemyDamagePercent} %");
            return Hint(x, y + 2f, width, "Only the host can change these.");
        }

        private static float RuleLine(float x, float y, string label, string value)
        {
            GUI.Label(Row(x, y, LabelWidth + 60f), label, _label);
            GUI.Label(new Rect(x + LabelWidth + 70f, y, 150f, RowHeight), value, _label);
            return y + RowHeight;
        }

        private static float EditRules(float x, float y, float width)
        {
            CoopPreferences.FriendlyFire = GUI.Toggle(Row(x, y, width), CoopPreferences.FriendlyFire, " Friendly fire", _toggle);
            y = Hint(x + 20f, y + RowHeight - 4f, width - 20f, "Players can hurt each other everywhere, not only in an arena ring.") + 4f;

            // Nothing below touches a world nobody shares.
            if (CoopSession.IsLive ? CoopSession.ArenaMode : CoopPreferences.ArenaMode) return y + RowGap;

            CoopPreferences.SharedLoot = GUI.Toggle(Row(x, y, width), CoopPreferences.SharedLoot, " Shared loot", _toggle);
            y = Hint(x + 20f, y + RowHeight - 4f, width - 20f, "What one player takes is gone for everyone.") + RowGap;

            CoopPreferences.RecoveryHealthPercent = (byte)Slider(x, y, width, "Health after getting up",
                CoopPreferences.RecoveryHealthPercent, 1, 100, " %");
            y += RowHeight + RowGap;

            CoopPreferences.EnemyHealthPercent = (ushort)Slider(x, y, width, "Enemy health",
                CoopPreferences.EnemyHealthPercent, CoopProtocol.MinEnemyHealthPercent, CoopProtocol.MaxEnemyHealthPercent, " %");
            y += RowHeight + RowGap;

            CoopPreferences.EnemyDamagePercent = (ushort)Slider(x, y, width, "Enemy damage",
                CoopPreferences.EnemyDamagePercent, CoopProtocol.MinEnemyDamagePercent, CoopProtocol.MaxEnemyDamagePercent, " %");
            return y + RowHeight + RowGap;
        }

        private static float DrawBackups(float x, float y, float width)
        {
            y = Paragraph(x, y, width, "A copy is kept each time the game overwrites a save. Restore one to undo what a session changed.", _label);

            bool canRestore = !Core.InGameplay;
            if (!canRestore) y = Hint(x, y - RowGap, width, "Go to the main menu to restore a save.") + RowGap;

            if (_confirm is { } confirm)
            {
                DateTime? when = confirm.BeforeSession ? confirm.Slot.BeforeSession : confirm.Slot.Latest;
                y = Paragraph(x, y, width, $"Replace {confirm.Hero}'s save {confirm.Slot.Index} with the copy from {When(when)}? " +
                    "The save as it is now is kept as a copy too.", _label);

                float confirmX = x;
                if (Action(ref confirmX, y, "Restore", _danger))
                {
                    SaveBackups.TryRestore(confirm.Slot.Hero, confirm.Slot.Index, confirm.BeforeSession ? "session" : "latest", out string message);
                    _lastLine = message;
                    _confirm = null;
                    LoadBackups();
                }

                if (Action(ref confirmX, y, "Cancel", _button)) _confirm = null;
                return y + RowHeight;
            }

            if (_backups.Count == 0)
            {
                y = Paragraph(x, y, width, "No backups yet. The first one is made the next time you save.", _hint);
            }
            else
            {
                const float entryHeight = (3f * RowHeight) + RowGap;
                float listHeight = Math.Min(BackupListHeight, _backups.Count * entryHeight);
                Rect view = new(0f, 0f, width - 20f, _backups.Count * entryHeight);
                _backupScroll = GUI.BeginScrollView(new Rect(x, y, width, listHeight), _backupScroll, view);

                float rowY = 0f;
                GUI.enabled = canRestore;
                foreach ((SaveBackups.Slot slot, string hero, bool played) in _backups)
                {
                    GUI.Label(Row(0f, rowY, view.width), $"{hero}, save {slot.Index}" + (played ? "   played together" : string.Empty), _label);
                    rowY += RowHeight;
                    rowY = BackupLine(rowY, view.width, "Before the last save", slot.Latest, slot, hero, beforeSession: false);
                    rowY = BackupLine(rowY, view.width, "Before a session", slot.BeforeSession, slot, hero, beforeSession: true);
                    rowY += RowGap;
                }

                GUI.enabled = true;
                GUI.EndScrollView();
                y += listHeight + RowGap;
            }

            float buttonX = x;
            if (Action(ref buttonX, y, "Back", _button))
            {
                _view = View.Start;
                _lastLine = string.Empty;
            }

            return y + RowHeight;
        }

        private static float BackupLine(float y, float width, string label, DateTime? when, SaveBackups.Slot slot, string hero, bool beforeSession)
        {
            if (when == null) return y;

            GUI.Label(new Rect(16f, y, width - SmallButtonWidth - 16f, RowHeight), $"{label}: {When(when)}", _hint);
            if (GUI.Button(new Rect(width - SmallButtonWidth - 8f, y + 2f, SmallButtonWidth, RowHeight - 4f), "Restore", _button))
            {
                _confirm = (slot, hero, beforeSession);
            }

            return y + RowHeight;
        }

        private static string When(DateTime? when) => when?.ToString("d MMM, HH:mm") ?? "-";

        private static void LoadBackups()
        {
            // Newest first: the save just played is the one most likely to need undoing.
            _backups = SaveBackups.Slots()
                .OrderByDescending(slot => slot.Latest ?? slot.BeforeSession)
                .Select(slot => (slot, SaveBackups.HeroName(slot.Hero) ?? $"Hero {slot.Hero}", CoopSaveMarks.IsMarked(slot.Hero, slot.Index)))
                .ToList();
        }

        private static string RulesSummary(bool friendlyFire, bool sharedLoot, int enemyHealth, int enemyDamage)
        {
            return $"friendly fire {(friendlyFire ? "on" : "off")}, {(sharedLoot ? "shared" : "own")} loot, " +
                $"enemies {enemyHealth} % health, {enemyDamage} % damage";
        }

        // Rounded to whole numbers, which is what the wire carries.
        private static int Slider(float x, float y, float width, string label, int value, int min, int max, string unit, string? zero = null)
        {
            GUI.Label(Row(x, y, LabelWidth), label, _label);

            float sliderX = x + LabelWidth;
            float sliderWidth = width - LabelWidth - ValueWidth - RowGap;
            float moved = GUI.HorizontalSlider(new Rect(sliderX, y + 8f, sliderWidth, RowHeight - 12f), value, min, max);

            GUI.Label(Row(sliderX + sliderWidth + RowGap, y, ValueWidth), value == 0 && zero != null ? zero : $"{value}{unit}", _label);

            return Math.Clamp((int)MathF.Round(moved), min, max);
        }

        private static bool Radio(float x, float y, float width, bool on, string title, string line)
        {
            bool clicked = GUI.Toggle(Row(x, y, width), on, " " + title, _toggle) && !on;
            GUI.Label(new Rect(x + 20f, y + RowHeight - 4f, width - 20f, HintHeight), line, _hint);
            return clicked;
        }

        private static float Foldout(float x, float y, float width, ref bool open, string text)
        {
            if (GUI.Button(Row(x, y, width), (open ? "▼  " : "►  ") + text, _link)) open = !open;
            return y + RowHeight + RowGap;
        }

        private static float Field(float x, float y, float width, string label, ref string value, int maxLength)
        {
            GUI.Label(Row(x, y, LabelWidth), label, _label);
            value = GUI.TextField(Row(x + LabelWidth, y, width - LabelWidth), value, maxLength, _field);
            return y + RowHeight + RowGap;
        }

        private static float Password(float x, float y, float width, string label)
        {
            GUI.Label(Row(x, y, LabelWidth), label, _label);
            _password = GUI.PasswordField(Row(x + LabelWidth, y, width - LabelWidth), _password, '*', MaxPasswordLength, _field);
            return y + RowHeight + RowGap;
        }

        private static void TabButton(ref float x, float y, Tab tab, string text)
        {
            const float tabWidth = 96f;
            bool selected = _tab == tab;
            if (GUI.Toggle(new Rect(x, y, tabWidth, RowHeight), selected, text, _tabStyle) && !selected) _tab = tab;
            if (selected) GUI.DrawTexture(new Rect(x + 8f, y + RowHeight - 1f, tabWidth - 16f, 3f), _lichenTexture);
            x += tabWidth + 4f;
        }

        private static bool Action(ref float x, float y, string text, GUIStyle? style)
        {
            bool pressed = GUI.Button(new Rect(x, y, ButtonWidth, RowHeight), text, style);
            x += ButtonWidth + RowGap;
            return pressed;
        }

        private static float Paragraph(float x, float y, float width, string text, GUIStyle? style)
        {
            float height = style!.CalcHeight(new GUIContent(text), width);
            GUI.Label(new Rect(x, y, width, height), text, style);
            return y + height + RowGap;
        }

        private static float Hint(float x, float y, float width, string text)
        {
            float height = _hint!.CalcHeight(new GUIContent(text), width);
            GUI.Label(new Rect(x, y, width, height), text, _hint);
            return y + height;
        }

        private static string Host()
        {
            Remember();
            if (!_direct) return CoopActions.Host(_ownCode);

            if (!int.TryParse(_portText.Trim(), out int port) || port < 1 || port > 65535) return "The port is a number from 1 to 65535.";

            _addresses = CoopActions.AddressLine(port);
            return CoopActions.HostDirect(port);
        }

        private static string Join()
        {
            Remember();
            if (_showAddress && _address.Trim().Length > 0) return CoopActions.JoinDirect(_address);
            if (_code.Length == 0) return "Enter the code your friend sent you, or pick a game from the list.";

            return CoopActions.Join(_code);
        }

        // CoopActions reads the password from the preferences, and coop_join falls back to the code there.
        private static void Remember()
        {
            _code = _code.Trim();
            CoopPreferences.RememberSession(_code, _password);
        }

        private static string PasteCode()
        {
            string clipboard = GUIUtility.systemCopyBuffer ?? string.Empty;
            clipboard = clipboard.Trim();

            if (clipboard.Length == 0) return "The clipboard is empty. Copy the code first.";
            if (clipboard.Length > MaxCodeLength) return "The clipboard does not hold a code.";

            _code = clipboard;
            return string.Empty;
        }

        private static string CopyCode()
        {
            string? code = CoopSession.SessionCode;
            if (code == null) return string.Empty;

            GUIUtility.systemCopyBuffer = code;
            return "Code copied.";
        }

        private static void SuppressGameplayInput()
        {
            InputAccess.ToggleGameplayActionMaps(false);
            _gameplayInputOff = true;
        }

        // Not gated on being in a world: the maps outlive the scene.
        private static void RestoreGameplayInput()
        {
            if (!_gameplayInputOff) return;
            _gameplayInputOff = false;

            try
            {
                InputAccess.ToggleGameplayActionMaps(true);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Coop] could not re-enable the gameplay input maps: " + e);
            }
        }

        private static Rect Row(float x, float y, float width)
        {
            return new Rect(x, y, width, RowHeight);
        }

        // Kept from unloading, so a scene change does not leave the styles pointing at destroyed textures.
        private static Texture2D Solid(Color color)
        {
            Texture2D texture = new(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private static GUIStyle Text(GUIStyle from, int size, Color color, FontStyle fontStyle = FontStyle.Normal)
        {
            GUIStyle style = new(from) { fontSize = size, fontStyle = fontStyle, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            style.normal.textColor = color;
            return style;
        }

        private static GUIStyle Filled(Color background, Color hover, Color text, TextAnchor alignment = TextAnchor.MiddleCenter)
        {
            GUIStyle style = new(GUI.skin.button) { fontSize = 14, alignment = alignment, border = new RectOffset(0, 0, 0, 0) };
            style.normal.background = Solid(background);
            style.hover.background = Solid(hover);
            style.active.background = Solid(hover);
            style.normal.textColor = text;
            style.hover.textColor = text;
            style.active.textColor = text;
            return style;
        }

        private static void EnsureStyles()
        {
            if (_title != null && _panelTexture != null) return;

            _panelTexture = Solid(Panel);
            _frameTexture = Solid(Frame);
            _lichenTexture = Solid(Lichen);

            _title = Text(GUI.skin.label, 18, Bone, FontStyle.Bold);
            _title.wordWrap = false;
            _label = Text(GUI.skin.label, 14, Bone);
            _hint = Text(GUI.skin.label, 12, Ash);
            _hint.alignment = TextAnchor.UpperLeft;

            _field = new GUIStyle(GUI.skin.textField) { fontSize = 14, alignment = TextAnchor.MiddleLeft, border = new RectOffset(0, 0, 0, 0) };
            _field.normal.background = Solid(FieldColor);
            _field.focused.background = Solid(FieldColor);
            _field.hover.background = Solid(FieldColor);
            _field.normal.textColor = Bone;
            _field.focused.textColor = Bone;
            _field.hover.textColor = Bone;

            _button = Filled(ButtonColor, ButtonHover, Bone);
            _primary = Filled(Lichen, new Color(0.635f, 0.718f, 0.416f, 1f), Panel);
            _primary.fontStyle = FontStyle.Bold;
            _danger = Filled(Rust, new Color(0.757f, 0.318f, 0.224f, 1f), Bone);
            _choice = Filled(ButtonColor, ButtonHover, Bone, TextAnchor.UpperLeft);

            _tabStyle = Filled(Panel, ButtonHover, Ash);
            _tabStyle.onNormal.background = _tabStyle.normal.background;
            _tabStyle.onHover.background = _tabStyle.hover.background;
            _tabStyle.onNormal.textColor = Bone;
            _tabStyle.onHover.textColor = Bone;

            _link = Filled(Panel, ButtonHover, Ash, TextAnchor.MiddleLeft);
            _link.fontSize = 13;

            _toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 14 };
            _toggle.normal.textColor = Bone;
            _toggle.onNormal.textColor = Bone;
            _toggle.hover.textColor = Bone;
            _toggle.onHover.textColor = Bone;
        }
    }
}
