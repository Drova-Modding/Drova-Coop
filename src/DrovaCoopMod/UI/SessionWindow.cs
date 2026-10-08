using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.UI
{
    /// <summary>
    /// The session window for hosting, joining, the session code, players and rules. IMGUI, because the game's
    /// options-menu builder has no text field. Gameplay input is off while it is open.
    /// </summary>
    internal static class SessionWindow
    {
        private const float Margin = 16f;
        private const float Width = 420f;
        private const float Padding = 12f;
        private const float RowHeight = 24f;
        private const float RowGap = 6f;
        private const float ButtonWidth = 96f;
        private const float RuleLabelWidth = 150f;
        private const float RuleValueWidth = 48f;

        private const int MaxCodeLength = 64;

        private const int MaxPasswordLength = 128;

        private static bool _seeded;
        private static string _code = string.Empty;
        private static string _password = string.Empty;
        private static string _lastLine = string.Empty;
        private static bool _drawFailed;
        private static bool _gameplayInputOff;

        // Out of a session the window first asks which, so a joining player never sees the host's settings.
        private static Choice _choice;

        // Where the previous pass's rows ended, since the box is drawn before the rows it sits behind.
        private static float _height;

        private static GUIStyle? _title;
        private static GUIStyle? _label;
        private static GUIStyle? _field;
        private static GUIStyle? _button;
        private static GUIStyle? _toggle;

        private enum Choice : byte { None, Host, Join }

        /// <summary>Whether the window is on screen.</summary>
        internal static bool Visible { get; private set; }

        private static void Show()
        {
            if (!_seeded)
            {
                _seeded = true;
                _code = CoopPreferences.SessionCode.Trim();
                _password = CoopPreferences.SessionPassword;
            }

            Visible = true;
        }

        private static void Hide()
        {
            Visible = false;
            RestoreGameplayInput();

            // Here rather than on each change: a slider drag is many changes.
            CoopPreferences.Save();
        }

        /// <summary>Shows or hides the window. Returns the line to tell the player.</summary>
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
            bool inSession = CoopSession.IsLive;
            bool idle = state == CoopSessionState.Idle || state == CoopSessionState.Failed;

            Rect box = new(Margin, Margin, Width, _height);
            GUI.Box(box, string.Empty);

            float x = box.x + Padding;
            float innerWidth = Width - (2f * Padding);
            float y = box.y + Padding;

            GUI.Label(Row(x, y, innerWidth), "Coop session", _title);
            y += RowHeight + RowGap;

            GUI.Label(Row(x, y, innerWidth), StatusLine(state), _label);
            y += RowHeight + RowGap;

            string? refusal = VersionCheck.Refusal;
            if (refusal != null)
            {
                GUI.Label(Row(x, y, innerWidth, 2f), refusal, _label);
                y += (2f * RowHeight) + RowGap;
            }

            if (inSession)
            {
                y = DrawSession(x, y, innerWidth);
            }
            else if (idle && _choice != Choice.None)
            {
                y = DrawEntry(x, y, innerWidth);
            }

            // The joining side's rules never count, so it is not shown them.
            if (state == CoopSessionState.Hosting || (idle && _choice == Choice.Host))
            {
                y = DrawRules(x, y, innerWidth);
            }

            y = DrawButtons(x, y, innerWidth, state, inSession, idle);

            if (_lastLine.Length > 0)
            {
                GUI.Label(Row(x, y, innerWidth, 2f), _lastLine, _label);
            }

            // Room for the result lines even when empty, so the box does not jump.
            _height = y + (2f * RowHeight) + Padding - box.y;
        }

        private static float DrawSession(float x, float y, float width)
        {
            string? code = CoopSession.SessionCode;
            if (code != null)
            {
                GUI.Label(Row(x, y, width), "Code: " + code, _title);
                y += RowHeight + RowGap;

                string sealing = CoopSession.IsEncrypted
                    ? "Encrypted. The relay cannot read this session."
                    : "NOT encrypted. The relay can read this session; set a password to seal it.";
                GUI.Label(Row(x, y, width), sealing, _label);
                y += RowHeight + RowGap;
            }

            foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
            {
                GUI.Label(Row(x, y, width), PlayerLine(player.Key, player.Value), _label);
                y += RowHeight;
            }

            if (CoopSession.Players.Count > 0) y += RowGap;

            return y;
        }

        private static float DrawEntry(float x, float y, float width)
        {
            const float labelWidth = 110f;
            float fieldX = x + labelWidth;
            float fieldWidth = width - labelWidth;

            GUI.Label(Row(x, y, labelWidth), "Session code", _label);
            _code = GUI.TextField(Row(fieldX, y, fieldWidth), _code, MaxCodeLength, _field);
            y += RowHeight + RowGap;

            GUI.Label(Row(x, y, labelWidth), "Password", _label);
            _password = GUI.PasswordField(Row(fieldX, y, fieldWidth), _password, '*', MaxPasswordLength, _field);
            y += RowHeight + RowGap;

            string hint = _choice == Choice.Host
                ? "Leave the code empty and the relay picks one. A password also encrypts the session."
                : "The host's code, and its password if it set one.";
            GUI.Label(Row(x, y, width), hint, _label);
            y += RowHeight + RowGap;

            return y;
        }

        private static float DrawRules(float x, float y, float width)
        {
            GUI.Label(Row(x, y, width), "Rules", _title);
            y += RowHeight + RowGap;

            CoopPreferences.FriendlyFire = GUI.Toggle(Row(x, y, width), CoopPreferences.FriendlyFire, " Friendly fire", _toggle);
            y += RowHeight;

            CoopPreferences.SharedLoot = GUI.Toggle(Row(x, y, width), CoopPreferences.SharedLoot, " Shared loot", _toggle);
            y += RowHeight + RowGap;

            CoopPreferences.RecoveryHealthPercent = (byte)Slider(x, y, width, "Health after getting up",
                CoopPreferences.RecoveryHealthPercent, 1, 100);
            y += RowHeight + RowGap;

            CoopPreferences.EnemyHealthPercent = (ushort)Slider(x, y, width, "Enemy health",
                CoopPreferences.EnemyHealthPercent, CoopProtocol.MinEnemyHealthPercent, CoopProtocol.MaxEnemyHealthPercent);
            y += RowHeight + RowGap;

            return y;
        }

        // Rounded to whole percent, which is what the wire carries.
        private static int Slider(float x, float y, float width, string label, int value, int min, int max)
        {
            GUI.Label(Row(x, y, RuleLabelWidth), label, _label);

            float sliderX = x + RuleLabelWidth;
            float sliderWidth = width - RuleLabelWidth - RuleValueWidth - RowGap;
            float moved = GUI.HorizontalSlider(new Rect(sliderX, y + 6f, sliderWidth, RowHeight - 12f), value, min, max);

            GUI.Label(Row(sliderX + sliderWidth + RowGap, y, RuleValueWidth), $"{value} %", _label);

            return Math.Clamp((int)MathF.Round(moved), min, max);
        }

        private static float DrawButtons(float x, float y, float width, CoopSessionState state, bool inSession, bool idle)
        {
            float buttonX = x;

            if (idle && _choice == Choice.None)
            {
                if (Button(ref buttonX, y, "Host")) _choice = Choice.Host;
                if (Button(ref buttonX, y, "Join")) _choice = Choice.Join;
            }
            else if (idle)
            {
                if (_choice == Choice.Host && Button(ref buttonX, y, "Host")) _lastLine = Host();
                if (_choice == Choice.Join && Button(ref buttonX, y, "Join")) _lastLine = Join();
                if (_choice == Choice.Join && Button(ref buttonX, y, "Paste code")) _lastLine = PasteCode();
                if (Button(ref buttonX, y, "Back")) _choice = Choice.None;
            }

            if (inSession && CoopSession.SessionCode != null)
            {
                if (Button(ref buttonX, y, "Copy code")) _lastLine = CopyCode();
            }

            if (inSession || state == CoopSessionState.Connecting)
            {
                if (Button(ref buttonX, y, "Leave")) _lastLine = CoopActions.Leave();
            }

            // Right-aligned, away from Leave.
            if (GUI.Button(new Rect(x + width - ButtonWidth, y, ButtonWidth, RowHeight), "Close", _button))
            {
                Hide();
            }

            return y + RowHeight + RowGap;
        }

        private static string Host()
        {
            Remember();
            return CoopActions.Host();
        }

        private static string Join()
        {
            Remember();
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

            if (clipboard.Length == 0) return "the clipboard is empty.";
            if (clipboard.Length > MaxCodeLength) return "the clipboard does not hold a session code.";

            _code = clipboard;
            return "code pasted. Join when ready.";
        }

        private static string CopyCode()
        {
            string? code = CoopSession.SessionCode;
            if (code == null) return "no session code to copy.";

            GUIUtility.systemCopyBuffer = code;
            return "code copied. Hand it to whoever is joining.";
        }

        private static string StatusLine(CoopSessionState state)
        {
            switch (state)
            {
                case CoopSessionState.Idle:
                    return "Not in a session.";
                case CoopSessionState.Connecting:
                    return "Connecting through " + CoopPreferences.RelayAddress + "...";
                case CoopSessionState.Hosting:
                    return "Hosting through " + CoopPreferences.RelayAddress + ".";
                case CoopSessionState.Joined:
                    return "Joined through " + CoopPreferences.RelayAddress + ".";
                case CoopSessionState.Failed:
                    return "Failed: " + (CoopSession.LastError ?? "no reason recorded.");
                default:
                    return state.ToString();
            }
        }

        private static string PlayerLine(PlayerId id, string name)
        {
            if (id == CoopSession.LocalPlayerId) return name + " (you)";

            float? ping = CoopSession.RoundTripMs(id);
            return ping.HasValue ? $"{name}, {ping.Value:0} ms" : name;
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

        private static bool Button(ref float x, float y, string text)
        {
            bool pressed = GUI.Button(new Rect(x, y, ButtonWidth, RowHeight), text, _button);
            x += ButtonWidth + RowGap;
            return pressed;
        }

        private static Rect Row(float x, float y, float width, float rows = 1f)
        {
            return new Rect(x, y, width, rows * RowHeight);
        }

        private static void EnsureStyles()
        {
            if (_title != null) return;

            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
            };

            _label = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = true,
            };

            _field = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
            };

            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
            };

            _toggle = new GUIStyle(GUI.skin.toggle)
            {
                fontSize = 13,
            };
        }
    }
}
