using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.UI
{
    /// <summary>
    /// The session window: where a player types the code a friend handed them, reads the code to hand on,
    /// and sees who is in the session and whether the relay can read it.
    ///
    /// Drawn with IMGUI, because the game's options-menu builder has no text field and a session code has
    /// to be typed somewhere findable. Opened from the coop section of the options window, from
    /// <c>coop_window</c>, or by <see cref="Toggle"/>. Escape closes it.
    ///
    /// IMGUI does not take keyboard focus away from the game, so while the window is open in a world the
    /// gameplay action maps are switched off through <see cref="InputAccess.ToggleGameplayActionMaps"/>,
    /// and switched back on when it closes. Otherwise typing a code would also move the player. What is
    /// typed is written back to the preferences on host or join, so the console commands and the next
    /// launch see the same code and password as the window.
    /// </summary>
    internal static class SessionWindow
    {
        private const float Margin = 16f;
        private const float Width = 420f;
        private const float Padding = 12f;
        private const float RowHeight = 24f;
        private const float RowGap = 6f;
        private const float ButtonWidth = 96f;

        /// <summary>
        /// Longer than any code a relay hands out, short enough that a paste of the wrong thing does not
        /// become a screen-wide field.
        /// </summary>
        private const int MaxCodeLength = 64;

        private const int MaxPasswordLength = 128;

        private static bool _seeded;
        private static string _code = string.Empty;
        private static string _password = string.Empty;
        private static string _lastLine = string.Empty;
        private static bool _drawFailed;
        private static bool _gameplayInputOff;

        private static GUIStyle? _title;
        private static GUIStyle? _label;
        private static GUIStyle? _field;
        private static GUIStyle? _button;

        /// <summary>
        /// Whether the window is on screen.
        /// </summary>
        internal static bool Visible { get; private set; }

        /// <summary>
        /// Shows the window. The code and password fields start from the preferences the first time, so a
        /// player who configured them by file finds them here rather than empty.
        /// </summary>
        internal static void Show()
        {
            if (!_seeded)
            {
                _seeded = true;
                _code = CoopPreferences.SessionCode.Trim();
                _password = CoopPreferences.SessionPassword;
            }

            Visible = true;
        }

        /// <summary>
        /// Hides the window. What was typed stays for the next <see cref="Show"/>.
        /// </summary>
        internal static void Hide()
        {
            Visible = false;
            RestoreGameplayInput();
        }

        /// <summary>
        /// Shows the window when hidden and hides it when shown. Returns the line to tell the player.
        /// </summary>
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

        /// <summary>
        /// Draws the window. Call from the melon's <c>OnGUI</c> on every event, not only repaints: the text
        /// fields and buttons read their input from the layout and input events.
        /// </summary>
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

                // Re-asserted on every repaint rather than once on show, because the game turns its maps
                // back on when one of its own menus closes, and the window may well outlive that menu.
                if (current != null && current.type == EventType.Repaint && Core.InGameplay)
                {
                    SuppressGameplayInput();
                }

                EnsureStyles();
                DrawContents();
            }
            catch (Exception e)
            {
                // Once, then never again this run. OnGUI runs several times a frame, and a failure here
                // would otherwise fill the log faster than anyone could read what went wrong.
                _drawFailed = true;
                Hide();
                MelonLogger.Error("[Coop] the session window failed to draw and stays closed: " + e);
            }
        }

        private static void DrawContents()
        {
            CoopSessionState state = CoopSession.State;
            bool inSession = state == CoopSessionState.Hosting || state == CoopSessionState.Joined;
            bool idle = state == CoopSessionState.Idle || state == CoopSessionState.Failed;

            float height = Height(state, inSession, idle);
            Rect box = new(Margin, Margin, Width, height);
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
            else if (idle)
            {
                y = DrawEntry(x, y, innerWidth);
            }

            y = DrawButtons(x, y, innerWidth, state, inSession, idle);

            if (_lastLine.Length > 0)
            {
                GUI.Label(Row(x, y, innerWidth, 2f), _lastLine, _label);
            }
        }

        /// <summary>
        /// The code to hand on, whether the relay can read the session, and who is in it.
        /// </summary>
        private static float DrawSession(float x, float y, float width)
        {
            string? code = CoopSession.SessionCode;
            if (code != null)
            {
                GUI.Label(Row(x, y, width), "Code: " + code, _title);
                y += RowHeight + RowGap;

                // Whether the relay can read the session is not a detail. Without a password the operator
                // sees everything, and a player deserves to know which of the two they are in.
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

        /// <summary>
        /// The two fields a player fills in before hosting or joining.
        /// </summary>
        private static float DrawEntry(float x, float y, float width)
        {
            const float labelWidth = 110f;
            float fieldX = x + labelWidth;
            float fieldWidth = width - labelWidth;

            GUI.Label(Row(x, y, labelWidth), "Session code", _label);
            _code = Cap(GUI.TextField(Row(fieldX, y, fieldWidth), _code, _field), MaxCodeLength);
            y += RowHeight + RowGap;

            GUI.Label(Row(x, y, labelWidth), "Password", _label);
            _password = Cap(GUI.PasswordField(Row(fieldX, y, fieldWidth), _password, '*', _field), MaxPasswordLength);
            y += RowHeight + RowGap;

            GUI.Label(Row(x, y, width),
                "Empty code when hosting lets the relay pick one. A password also encrypts the session.", _label);
            y += RowHeight + RowGap;

            return y;
        }

        private static float DrawButtons(float x, float y, float width, CoopSessionState state, bool inSession, bool idle)
        {
            float buttonX = x;

            if (idle)
            {
                if (Button(ref buttonX, y, "Host")) _lastLine = Host();
                if (Button(ref buttonX, y, "Join")) _lastLine = Join();
                if (Button(ref buttonX, y, "Paste code")) _lastLine = PasteCode();
            }

            if (inSession && CoopSession.SessionCode != null)
            {
                if (Button(ref buttonX, y, "Copy code")) _lastLine = CopyCode();
            }

            if (inSession || state == CoopSessionState.Connecting)
            {
                if (Button(ref buttonX, y, "Leave")) _lastLine = CoopActions.Leave();
            }

            // Right-aligned, apart from the actions, so the button that ends the window is not the one
            // beside the button that ends the session.
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

        /// <summary>
        /// Writes the fields back to the preferences before acting, because <see cref="CoopActions"/> reads
        /// the password from there and the console's <c>coop_join</c> falls back to the code there.
        /// </summary>
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

        /// <summary>
        /// The box height for what is about to be drawn. Computed ahead of drawing because the box has to
        /// go on screen before the rows it sits behind.
        /// </summary>
        private static float Height(CoopSessionState state, bool inSession, bool idle)
        {
            float rows = 2f;
            float gaps = 2f;

            if (VersionCheck.Refusal != null)
            {
                rows += 2f;
                gaps += 1f;
            }

            if (inSession)
            {
                if (CoopSession.SessionCode != null)
                {
                    rows += 2f;
                    gaps += 2f;
                }

                rows += CoopSession.Players.Count;
                if (CoopSession.Players.Count > 0) gaps += 1f;
            }
            else if (idle)
            {
                rows += 3f;
                gaps += 3f;
            }

            // The buttons, and the two lines kept for the last result even when it is empty, so the box
            // does not jump when a button is pressed.
            rows += 3f;
            gaps += 1f;

            return (2f * Padding) + (rows * RowHeight) + (gaps * RowGap);
        }

        private static void SuppressGameplayInput()
        {
            InputAccess.ToggleGameplayActionMaps(false);
            _gameplayInputOff = true;
        }

        /// <summary>
        /// Turns the gameplay maps back on if this window turned them off. Not gated on being in a world:
        /// the maps belong to the input player, which outlives the scene, so a window closed in the main
        /// menu after the world ended still has to give them back.
        /// </summary>
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

        private static string Cap(string text, int max)
        {
            if (text == null) return string.Empty;
            return text.Length > max ? text.Substring(0, max) : text;
        }

        /// <summary>
        /// Builds the styles on first draw. A GUIStyle cannot be created outside a GUI call.
        /// </summary>
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
        }
    }
}
