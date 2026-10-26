using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Draws each remote player's name above their body, with IMGUI since the game's prefab-built UI has no
    /// runtime surface for new elements.
    /// </summary>
    internal sealed class NameTags : ICoopSystem
    {
        /// <summary>
        /// Label height above a body's origin, in world units. Character sprites use one pixel per unit, so
        /// this clears a body of about 35 pixels above its pivot.
        /// </summary>
        private const float HeightOffset = 48f;

        /// <summary>Off-screen slack in pixels, so a label does not flicker out at the view's edge.</summary>
        private const float ScreenMargin = 64f;

        private const int LabelWidth = 220;
        private const int LabelHeight = 20;

        private static readonly Dictionary<string, GUIContent> ContentByName = new();

        private static GUIStyle? _style;
        private static Camera? _camera;

        /// <summary>Draws the labels. Call from the melon's <c>OnGUI</c>.</summary>
        internal static void Draw()
        {
            if (!CoopSession.IsLive) return;
            if (RemotePlayers.Count == 0) return;

            if (Event.current == null || Event.current.type != EventType.Repaint) return;

            Camera? camera = ActiveCamera();
            if (camera == null) return;

            try
            {
                foreach (KeyValuePair<PlayerId, RemotePlayer> remote in RemotePlayers.Bodies)
                {
                    Actor body = remote.Value.Body;
                    if (body == null) continue;

                    string name = CoopSession.Players.TryGetValue(remote.Key, out string known) ? known : remote.Key.ToString();
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

            if (screen.z <= 0f) return;

            // Culled against the screen, not world distance, since the visible area depends on the camera.
            if (screen.x < -ScreenMargin || screen.x > Screen.width + ScreenMargin) return;
            if (screen.y < -ScreenMargin || screen.y > Screen.height + ScreenMargin) return;

            Rect area = new(screen.x - (LabelWidth / 2f), Screen.height - screen.y - LabelHeight, LabelWidth, LabelHeight);

            // One black shadow keeps the label readable over Drova's dark world at two interop calls, not five.
            GUIStyle style = Style();

            // Held per name, so the string is not converted to the game's twice a frame.
            if (!ContentByName.TryGetValue(name, out GUIContent? content))
            {
                content = new GUIContent(name);
                ContentByName[name] = content;
            }

            GUI.color = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), content, style);

            GUI.color = Color.white;
            GUI.Label(area, content, style);
        }

        private static Camera? ActiveCamera()
        {
            if (_camera != null) return _camera;

            _camera = Camera.main;
            return _camera;
        }

        /// <summary>Forgets the cached camera. Called when the world goes away.</summary>
        private static void Reset()
        {
            _camera = null;
        }

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

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }

        void ICoopSystem.EndSession()
        {
            ContentByName.Clear();
        }
    }
}
