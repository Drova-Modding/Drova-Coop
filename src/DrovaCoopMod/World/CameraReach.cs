using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova.AI.Modules;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps the world switched on around a player the camera is not looking at.
    ///
    /// Drova turns a creature off by distance to the camera, and there is one camera. The gate is
    /// <c>AI_ActorFactory.IsChangeToActorPossible</c>: failing it runs <c>ChangeToRef</c>, which calls
    /// <c>gameObject.SetActive(false)</c> on the whole actor. The behavior tree, the target service,
    /// perception and the hit system all stop in that one step.
    ///
    /// The hit system's own range checks, <c>HitReceiveHandler.IsGameInRange</c> and
    /// <c>HitTriggerHandler.IsPlayerInRange</c>, need no patch of their own. The shipping build does
    /// not reach them, and a loaded actor keeps its hit handlers with it. A remote player's puppet does
    /// not rely on this either. Its receivers are pinned at creation, see PuppetFactory.
    ///
    /// Every patch here can only turn a no into a yes. The game's own answer is never overruled in
    /// the other direction, so nothing here can switch off something the game wanted on, and with nobody
    /// else in the session every one of them returns on a count.
    ///
    /// The cost is real and is the point: a creature held this way is a body the host builds, ticks and
    /// simulates for somebody else's benefit. What bounds it is that a remote player is one player
    /// standing in one place, so the set is the same size the host's own surroundings are.
    /// </summary>
    internal static class CameraReach
    {
        private static bool _reported;

        /// <summary>
        /// How many times the AI load gate was consulted, counted apart from the times it mattered. For
        /// <c>coop_status</c>. Zero means the gate was inlined: IsChangeToActorPossible is private with three
        /// call sites inside its own class, the exact shape that disappears.
        /// </summary>
        internal static int AskedActor { get; private set; }

        /// <summary>How many times a creature was left as a body for a remote player. For <c>coop_status</c>.</summary>
        internal static int KeptActor { get; private set; }

        /// <summary>How many times putting a creature away was refused outright. For <c>coop_status</c>.</summary>
        internal static int RefusedRef { get; private set; }

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(AI_ActorFactory), "IsChangeToActorPossible",
                typeof(CameraReach), nameof(IsChangeToActorPossiblePostfix));

            // A second answer to the same question, at the place the decision is carried out rather than
            // where it is made. If the test above was folded into its callers the patch on it applies and
            // never runs, and the only sign of that is a creature quietly going inert; this one is reached
            // from Unload, which the lazy manager calls through ILazyObject, so it cannot be inlined away.
            Hooking.TryPrefix(harmony, typeof(AI_ActorFactory), "ChangeToRef",
                typeof(CameraReach), nameof(ChangeToRefPrefix));
        }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            AskedActor = 0;
            KeptActor = 0;
            RefusedRef = 0;
        }

        /// <summary>
        /// Says a creature is worth building when a remote player is standing next to it.
        ///
        /// The game's own activation distance rather than a number of this mod's choosing, read off the
        /// instance - it is a serialized field, so a creature authored to wake early or late keeps that
        /// property for the second player as well.
        /// </summary>
        private static void IsChangeToActorPossiblePostfix(AI_ActorFactory __instance, ref bool __result)
        {
            try
            {
                AskedActor++;

                if (__result) return;
                if (!CoopSession.IsLive) return;
                if (RemotePlayers.Count == 0) return;

                Il2CppDrova.Actor actor = __instance._actor;
                if (actor == null) return;

                if (!RemotePlayers.IsAnyWithin(actor.GetFeetPosition(), __instance._npcActivateDistance)) return;

                __result = true;
                KeptActor++;

                Announce();
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside the loader's own sweep over every AI actor in the world, and
                // an exception let out would stop that sweep for everything after it.
                MelonLogger.Error("[Enemy] deciding whether a creature is worth building failed: " + e);
            }
        }

        /// <summary>
        /// Refuses to put a creature away while a remote player is standing next to it.
        ///
        /// <c>ChangeToRef</c> is where the deactivation happens, and it is reached by two routes: the
        /// factory's own state update, and <c>Unload</c> when the lazy manager decides on the priority the
        /// test above produced. Refusing here covers both, including the case the mod cannot otherwise
        /// reach - <c>UpdateState</c> never consults <c>_stayActive</c>, so the pin this mod applies to a
        /// claimed creature does not survive anything that calls <c>ForceUpdateState</c>, and loading an
        /// actor calls it.
        /// </summary>
        /// <returns>False to skip the original, which leaves the creature as a live body.</returns>
        private static bool ChangeToRefPrefix(AI_ActorFactory __instance)
        {
            try
            {
                if (!CoopSession.IsLive) return true;
                if (RemotePlayers.Count == 0) return true;

                Il2CppDrova.Actor actor = __instance._actor;
                if (actor == null) return true;

                if (!RemotePlayers.IsAnyWithin(actor.GetFeetPosition(), __instance._npcActivateDistance)) return true;

                RefusedRef++;

                Announce();

                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] refusing to put a creature away failed: " + e);

                // The game's own behavior on any doubt. A creature wrongly put away is a creature that
                // comes back; a creature wrongly kept is a leak for as long as the session lasts.
                return true;
            }
        }

        /// <summary>
        /// Said once per session, the first time the gate is widened. It fires thousands of times a
        /// minute after that, so the counts in <c>coop_status</c> are where the detail belongs.
        /// </summary>
        private static void Announce()
        {
            if (_reported) return;

            _reported = true;

            MelonLogger.Msg("[Enemy] the world is being kept switched on around a remote player. Drova " +
                "measures what to build from the camera, and there is one camera - so without this the " +
                "far end of a session is scenery.");
        }
    }
}
