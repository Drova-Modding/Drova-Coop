using Drova_Modding_API;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova.AI.Modules;
using Il2CppDrova.HitSystem;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Keeps creatures switched on around a remote player, since Drova deactivates actors by distance to the one camera.
    /// Patches only ever turn a no into a yes.
    /// </summary>
    internal sealed class CameraReach : ICoopSystem
    {
        private static bool _reported;

        /// <summary>Times IsChangeToActorPossible ran. Zero means it was inlined.</summary>
        internal static int AskedActor { get; private set; }

        /// <summary>Creatures built as bodies for a remote player.</summary>
        internal static int KeptActor { get; private set; }

        /// <summary>Times putting a creature away was refused.</summary>
        internal static int RefusedRef { get; private set; }

        /// <summary>Hit boxes kept live for a remote player.</summary>
        internal static int KeptTrigger { get; private set; }

        /// <summary>Hit receivers kept live for a remote player.</summary>
        internal static int KeptReceiver { get; private set; }

        /// <summary>The game's own margin around a hit box, past which the camera no longer keeps it loaded.</summary>
        private const float TriggerMargin = 300f;

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(AI_ActorFactory), "IsChangeToActorPossible",
                typeof(CameraReachPatches), nameof(CameraReachPatches.IsChangeToActorPossiblePostfix));

            // Backup in case the test above was inlined.
            Hooking.TryPrefix(harmony, typeof(AI_ActorFactory), "ChangeToRef",
                typeof(CameraReachPatches), nameof(CameraReachPatches.ChangeToRefPrefix));

            Hooking.TryPostfix(harmony, typeof(HitTriggerHandler), nameof(HitTriggerHandler.GetLoadPriority),
                typeof(CameraReachPatches), nameof(CameraReachPatches.TriggerLoadPriorityPostfix));

            // A blow needs the receiving end live too.
            Hooking.TryPostfix(harmony, typeof(HitReceiveHandler), nameof(HitReceiveHandler.GetLoadPriority),
                typeof(CameraReachPatches), nameof(CameraReachPatches.ReceiverLoadPriorityPostfix));
        }

        private static void Reset()
        {
            AskedActor = 0;
            KeptActor = 0;
            RefusedRef = 0;
            KeptTrigger = 0;
            KeptReceiver = 0;
        }

        /// <summary>Builds a creature near a remote player, using its own authored activation distance.</summary>
        internal static void IsChangeToActorPossiblePostfix(AI_ActorFactory __instance, ref bool __result)
        {
            try
            {
                AskedActor++;

                if (__result) return;
                if (!CoopSession.SharesWorld) return;
                if (RemotePlayers.Count == 0) return;

                Il2CppDrova.Actor actor = __instance._actor;
                if (actor == null) return;

                // Only distance is widened. An actor the story has not switched on, like a boss fight's waiting waves, stays off.
                if (__instance._openWorldRefObject == null || !actor.IsActiveInGame) return;

                if (!RemotePlayers.IsAnyWithin(actor.GetFeetPosition(), __instance._npcActivateDistance)) return;

                __result = true;
                KeptActor++;

                Announce();
            }
            catch (Exception e)
            {
                // A throw here would stop the loader's sweep over every AI actor.
                MelonLogger.Error("[Enemy] deciding whether a creature is worth building failed: " + e);
            }
        }

        /// <summary>
        /// Refuses to deactivate a creature near a remote player.
        /// Also covers <c>ForceUpdateState</c> on load, which ignores the <c>_stayActive</c> pin.
        /// </summary>
        internal static bool ChangeToRefPrefix(AI_ActorFactory __instance)
        {
            try
            {
                if (!CoopSession.SharesWorld) return true;
                if (RemotePlayers.Count == 0) return true;

                Il2CppDrova.Actor actor = __instance._actor;
                if (actor == null) return true;
                if (!actor.IsActiveInGame) return true;

                if (!RemotePlayers.IsAnyWithin(actor.GetFeetPosition(), __instance._npcActivateDistance)) return true;

                RefusedRef++;

                Announce();

                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] refusing to put a creature away failed: " + e);

                // A creature wrongly put away comes back. One wrongly kept leaks for the session.
                return true;
            }
        }

        /// <summary>
        /// Keeps a hit box live near a remote player.
        /// The loader switches hit boxes off beyond 300 of the camera, so a teammate off the host's screen could not be hit.
        /// </summary>
        internal static void TriggerLoadPriorityPostfix(HitTriggerHandler __instance, ref LazyPriority __result)
        {
            try
            {
                if (__result != LazyPriority.Idle) return;
                if (!CoopSession.SharesWorld || RemotePlayers.Count == 0) return;
                if (__instance == null || !__instance.gameObject.activeInHierarchy) return;

                Vector2 middle = __instance._colliderLocalMidPoint + (Vector2)__instance.transform.position;
                if (!RemotePlayers.IsAnyWithin(middle, __instance._colliderMaxRadius + TriggerMargin)) return;

                __result = LazyPriority.Primary;
                KeptTrigger++;
            }
            catch
            {
                // Runs for every hit box on every streaming pass. The box keeps the game's own priority.
            }
        }

        /// <summary>
        /// Keeps a body's hit receiver live near a remote player.
        /// Player-only trigger volumes are left to <see cref="World.GuardReach"/>.
        /// </summary>
        internal static void ReceiverLoadPriorityPostfix(HitReceiveHandler __instance, ref LazyPriority __result)
        {
            try
            {
                if (__result != LazyPriority.Idle) return;
                if (!CoopSession.SharesWorld || RemotePlayers.Count == 0) return;
                if (__instance == null || __instance._onlyCheckWithPlayer || !__instance.gameObject.activeInHierarchy) return;

                Rect rect = __instance._localRect;
                Vector2 middle = rect.center + (Vector2)__instance.transform.position;
                if (!RemotePlayers.IsAnyWithin(middle, Mathf.Max(rect.width, rect.height) + TriggerMargin)) return;

                __result = LazyPriority.Primary;
                KeptReceiver++;
            }
            catch
            {
                // Runs for every receiver on every streaming pass. The receiver keeps the game's own priority.
            }
        }

        /// <summary>Logs once per session.</summary>
        private static void Announce()
        {
            if (_reported) return;

            _reported = true;

            MelonLogger.Msg("[Enemy] the world is being kept switched on around a remote player. Drova " +
                "measures what to build from the camera, and there is one camera - so without this the " +
                "far end of a session is scenery.");
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
