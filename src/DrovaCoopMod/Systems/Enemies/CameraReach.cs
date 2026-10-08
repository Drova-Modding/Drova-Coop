using Drova_Modding_API;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova.AI.Modules;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Keeps creatures switched on around a remote player, since Drova deactivates actors by distance to the one camera.
    /// Patches only ever turn a no into a yes.
    /// </summary>
    internal sealed class CameraReach : ICoopSystem
    {
        private static bool _reported;

        /// <summary>
        /// Times the AI load gate was consulted, for <c>coop_status</c>. Zero means IsChangeToActorPossible was inlined.
        /// </summary>
        internal static int AskedActor { get; private set; }

        /// <summary>How many times a creature was left as a body for a remote player. For <c>coop_status</c>.</summary>
        internal static int KeptActor { get; private set; }

        /// <summary>How many times putting a creature away was refused outright. For <c>coop_status</c>.</summary>
        internal static int RefusedRef { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(AI_ActorFactory), "IsChangeToActorPossible",
                typeof(CameraReachPatches), nameof(CameraReachPatches.IsChangeToActorPossiblePostfix));

            // Backup at the place the decision is carried out, in case the test above was inlined.
            // Reached from Unload through ILazyObject, so it cannot be inlined away.
            Hooking.TryPrefix(harmony, typeof(AI_ActorFactory), "ChangeToRef",
                typeof(CameraReachPatches), nameof(CameraReachPatches.ChangeToRefPrefix));
        }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        private static void Reset()
        {
            AskedActor = 0;
            KeptActor = 0;
            RefusedRef = 0;
        }

        /// <summary>Builds a creature near a remote player, using its own authored activation distance.</summary>
        internal static void IsChangeToActorPossiblePostfix(AI_ActorFactory __instance, ref bool __result)
        {
            try
            {
                AskedActor++;

                if (__result) return;
                if (!CoopSession.IsLive) return;
                if (RemotePlayers.Count == 0) return;

                Il2CppDrova.Actor actor = __instance._actor;
                if (actor == null) return;

                // Only distance is widened. An actor the story has not switched on (a boss fight's waiting waves) stays off.
                if (__instance._openWorldRefObject == null || !actor.IsActiveInGame) return;

                if (!RemotePlayers.IsAnyWithin(actor.GetFeetPosition(), __instance._npcActivateDistance)) return;

                __result = true;
                KeptActor++;

                Announce();
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside the loader's sweep over every AI actor, a throw would stop it.
                MelonLogger.Error("[Enemy] deciding whether a creature is worth building failed: " + e);
            }
        }

        /// <summary>
        /// Refuses to deactivate a creature near a remote player. Covers both callers of <c>ChangeToRef</c>,
        /// and <c>ForceUpdateState</c> on load, which ignores the <c>_stayActive</c> pin.
        /// </summary>
        /// <returns>False to skip the original, which leaves the creature as a live body.</returns>
        internal static bool ChangeToRefPrefix(AI_ActorFactory __instance)
        {
            try
            {
                if (!CoopSession.IsLive) return true;
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

                // A creature wrongly put away comes back, one wrongly kept leaks for the session.
                return true;
            }
        }

        /// <summary>Logs once per session. Detail is in the <c>coop_status</c> counts.</summary>
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
