using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Draws each remote player's name above their body.
    ///
    /// Remote bodies are clones of the same prefab, so at three or four players there is nothing on screen
    /// that distinguishes one from another. The roster answers who is connected; only a label answers which
    /// of the figures in front of you is which.
    ///
    /// Drawn with IMGUI rather than through the game's own UI. The game builds its interface from prefabs
    /// laid out in the editor, and there is no runtime surface for adding an element to them; IMGUI needs
    /// nothing but a rectangle and survives the module stripping. It is not how a shipped interface should
    /// be built - a lobby panel belongs in the game's own options-menu system - but a floating label over a
    /// character is not part of any panel.
    /// </summary>
    internal static class NameTags
    {
        /// <summary>
        /// How far above a body's origin the label sits, in world units.
        ///
        /// Taken from the art rather than measured at runtime or guessed from a screenshot. The character
        /// sprites are authored at <c>m_PixelsToUnits: 1</c>, so a world unit is a source pixel: a body
        /// piece such as <c>Human_Chest_Atlanter_Druid_Idle_NE_0</c> is 39 pixels tall with its pivot at
        /// 0.103 of that, putting the origin about four units up from the base and the body about
        /// thirty-five units above the origin. Rounded up to clear the head and hair.
        ///
        /// A constant because that number is a property of the art, and the art does not change while the
        /// game runs. Measuring renderer bounds instead would walk every child of every body to rediscover
        /// it.
        /// </summary>
        private const float HeightOffset = 48f;

        /// <summary>
        /// How far outside the screen a label may still be drawn, in pixels. Slightly off-screen is worth
        /// keeping: it stops a label flickering out the instant a player touches the edge of the view.
        /// </summary>
        private const float ScreenMargin = 64f;

        private const int LabelWidth = 220;
        private const int LabelHeight = 20;

        private static GUIStyle? _style;
        private static Camera? _camera;

        /// <summary>
        /// Draws the labels. Call from the melon's <c>OnGUI</c>.
        /// </summary>
        internal static void Draw()
        {
            // OnGUI runs several times a frame - once to lay out, once to repaint, and once per input
            // event - and only the repaint puts anything on screen. Everything below this line used to run
            // for all of them, which is most of what made drawing a handful of labels expensive.
            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            if (!CoopSession.IsLive) return;
            if (RemotePlayers.Count == 0) return;

            Camera camera = ActiveCamera();
            if (camera == null) return;

            try
            {
                foreach (KeyValuePair<PlayerId, RemotePlayer> remote in RemotePlayers.Bodies)
                {
                    Actor body = remote.Value.Body;
                    if (body == null) continue;

                    string name = CoopSession.Players.TryGetValue(remote.Key, out string? known) ? known : remote.Key.ToString();
                    DrawLabel(camera, body.transform.position, name);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Coop] drawing name tags failed: " + e);
            }
        }

        private static void DrawLabel(Camera camera, Vector3 worldPosition, string name)
        {
            Vector3 head = worldPosition + new Vector3(0f, HeightOffset, 0f);
            Vector3 screen = camera.WorldToScreenPoint(head);

            // Negative z means the point is behind the camera, where WorldToScreenPoint still returns
            // coordinates on screen - drawing those puts a label over a character nobody can see.
            if (screen.z <= 0f) return;

            // Culled against the screen rather than by world distance. Distance sounds equivalent and is
            // not: how much world fits on screen depends on the camera, so any fixed radius is either
            // wider than the view - drawing labels for players nobody can see - or narrower, which hides
            // the label of somebody standing in plain sight.
            if (screen.x < -ScreenMargin || screen.x > Screen.width + ScreenMargin) return;
            if (screen.y < -ScreenMargin || screen.y > Screen.height + ScreenMargin) return;

            Rect area = new(screen.x - (LabelWidth / 2f), Screen.height - screen.y - LabelHeight, LabelWidth, LabelHeight);

            // One offset copy in black behind the text, not an outline of four. Drova's world is dark and
            // heavily textured, so a single-color label is unreadable over parts of it - but each Label is
            // an interop call, and a full outline made that five per player per repaint to solve a problem
            // one shadow already solves.
            GUIStyle style = Style();

            GUI.color = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), name, style);

            GUI.color = Color.white;
            GUI.Label(area, name, style);
        }

        /// <summary>
        /// The camera to project through, cached.
        ///
        /// <c>Camera.main</c> is not a field: it searches the scene for an object tagged MainCamera on
        /// every access, and this is on a path that runs every frame. Re-fetched only when the cached one
        /// has been destroyed, which is what happens across a scene change.
        /// </summary>
        private static Camera? ActiveCamera()
        {
            if (_camera != null) return _camera;

            _camera = Camera.main;
            return _camera;
        }

        /// <summary>
        /// Forgets the cached camera. Called when the world goes away, since the next one belongs to a
        /// different scene.
        /// </summary>
        internal static void Reset()
        {
            _camera = null;
        }

        /// <summary>
        /// The label style, built once. A GUIStyle cannot be created outside a GUI call, so it is made on
        /// first draw rather than at initialization.
        /// </summary>
        private static GUIStyle Style()
        {
            if (_style != null) return _style;

            _style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
            };

            return _style;
        }
    }
}
