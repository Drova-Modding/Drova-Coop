using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova.AI.Modules;
using Il2CppDrova.HitSystem;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps the world switched on around a player the camera is not looking at.
    ///
    /// Drova turns most of a creature off by distance to the camera, and there is one camera. Three
    /// separate systems do it, each with its own radius, and all three read the same field:
    /// <code>
    /// Vector2 camPos2D = CameraGameHandler.CamPos2D;
    /// </code>
    /// Which is right for a game with one person in it, and is the reason a client fighting at the other
    /// end of the map is fighting furniture:
    ///
    /// - <c>AI_ActorFactory.IsChangeToActorPossible</c> at 600 units decides whether a creature is a body
    ///   or a marker. Failing it runs <c>ChangeToRef</c>, which calls <c>gameObject.SetActive(false)</c>
    ///   on the whole actor - so the behavior tree, the target service, perception and the hit system all
    ///   stop in one step, and <c>HitTriggerBhvr_ActorCognition.OnDisable</c> takes the creature out of the
    ///   cognition manager on the way past, so nothing else can notice it either.
    /// - <c>HitReceiveHandler.IsGameInRange</c> at 300 units decides whether a body can be struck. Failing
    ///   it disables the receive collider and stops simulating its rigidbody, which is what makes a remote
    ///   player unhittable rather than merely hard to hit.
    /// - <c>HitTriggerHandler.IsPlayerInRange</c> at the weapon's own radius plus 300 decides whether a
    ///   swing exists at all. Failing it disables the component and its collider, so a creature plays its
    ///   whole attack animation and connects with nothing.
    ///
    /// Together they are the reported symptom exactly: creatures near the client wander instead of
    /// fighting, and the one that does attack passes straight through.
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
        /// <summary>
        /// The hit system's own activation distance, which it holds as a constant rather than a field.
        /// </summary>
        private const float HitReach = 300f;

        // Counted separately from the times it mattered. A patch that never fires and a patch that fires
        // and never changes anything read identically from the outside, and this build inlines small
        // methods into their callers - IsChangeToActorPossible is private with three call sites inside
        // its own class, which is the exact shape that disappears. Asked at zero is the diagnosis.
        private static int _askedActor;
        private static int _keptActor;
        private static int _refusedRef;
        private static int _askedReceive;
        private static int _keptReceive;
        private static int _askedTrigger;
        private static int _keptTrigger;

        private static bool _reported;

        /// <summary>How many times the AI load gate was consulted. For <c>coop_status</c>.</summary>
        internal static int AskedActor => _askedActor;

        /// <summary>How many times a creature was left as a body for a remote player. For <c>coop_status</c>.</summary>
        internal static int KeptActor => _keptActor;

        /// <summary>How many times putting a creature away was refused outright. For <c>coop_status</c>.</summary>
        internal static int RefusedRef => _refusedRef;

        /// <summary>How many times the hit-receive gate was consulted. For <c>coop_status</c>.</summary>
        internal static int AskedReceive => _askedReceive;

        /// <summary>How many times a body was left hittable for a remote player. For <c>coop_status</c>.</summary>
        internal static int KeptReceive => _keptReceive;

        /// <summary>How many times the weapon gate was consulted. For <c>coop_status</c>.</summary>
        internal static int AskedTrigger => _askedTrigger;

        /// <summary>How many times a swing was left able to connect. For <c>coop_status</c>.</summary>
        internal static int KeptTrigger => _keptTrigger;

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

            Hooking.TryPostfix(harmony, typeof(HitReceiveHandler), "IsGameInRange",
                typeof(CameraReach), nameof(IsGameInRangePostfix));

            Hooking.TryPostfix(harmony, typeof(HitTriggerHandler), "IsPlayerInRange",
                typeof(CameraReach), nameof(IsPlayerInRangePostfix));
        }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            _askedActor = 0;
            _keptActor = 0;
            _refusedRef = 0;
            _askedReceive = 0;
            _keptReceive = 0;
            _askedTrigger = 0;
            _keptTrigger = 0;
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
                _askedActor++;

                if (__result) return;
                if (!InSession()) return;
                if (RemotePlayers.Count == 0) return;

                Il2CppDrova.Actor actor = __instance._actor;
                if (actor == null) return;

                if (!RemotePlayers.IsAnyWithin(actor.GetFeetPosition(), __instance._npcActivateDistance)) return;

                __result = true;
                _keptActor++;

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
                if (!InSession()) return true;
                if (RemotePlayers.Count == 0) return true;

                Il2CppDrova.Actor actor = __instance._actor;
                if (actor == null) return true;

                if (!RemotePlayers.IsAnyWithin(actor.GetFeetPosition(), __instance._npcActivateDistance)) return true;

                _refusedRef++;

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
        /// Keeps a body hittable while a remote player is near it.
        ///
        /// This is what a remote player's own body needs most. A puppet is a clone of the player
        /// prefab, so its hit receivers register with the same loader as everything else, and on the host
        /// they are more than 300 units from the host's camera whenever the two players are apart. The
        /// receiver's <c>Unload</c> then does
        /// <code>
        /// receiveCollider.enabled = false;
        /// rigidbody.simulated = false;
        /// </code>
        /// which is the difference between a teammate who is hard to hit and one who cannot be hit at all,
        /// and is why a creature was seen attacking a client who took no damage from it.
        ///
        /// The game's own rectangle rather than a radius: it is the receiver's collider bounds grown by
        /// the activation distance, so a large body is covered the way the game covers it.
        /// </summary>
        private static void IsGameInRangePostfix(HitReceiveHandler __instance, ref bool __result)
        {
            try
            {
                _askedReceive++;

                if (__result) return;
                if (!InSession()) return;
                if (RemotePlayers.Count == 0) return;

                Rect reach = __instance._localRect;
                Vector3 here = __instance.transform.position;
                reach.center += new Vector2(here.x, here.y);

                IReadOnlyList<Vector2> players = RemotePlayers.Positions;

                for (int index = 0; index < players.Count; index++)
                {
                    if (!reach.Contains(players[index])) continue;

                    __result = true;
                    _keptReceive++;

                    Announce();
                    return;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] deciding whether a body can still be struck failed: " + e);
            }
        }

        /// <summary>
        /// Keeps a swing able to connect while a remote player is near it.
        ///
        /// The mirror of the receiver: without it a creature the host is simulating for the client plays
        /// its attack with the collider that carries the blow switched off, and the component disabled
        /// besides. The margin is the game's own - the weapon's widest collider plus the activation
        /// distance, recomputed by the method this follows, so reading the field here is reading what it
        /// just measured.
        /// </summary>
        private static void IsPlayerInRangePostfix(HitTriggerHandler __instance, ref bool __result)
        {
            try
            {
                _askedTrigger++;

                if (__result) return;
                if (!InSession()) return;
                if (RemotePlayers.Count == 0) return;

                if (!RemotePlayers.IsAnyWithin(__instance.GetWorldMidPoint(), __instance._colliderMaxRadius + HitReach))
                {
                    return;
                }

                __result = true;
                _keptTrigger++;

                Announce();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] deciding whether a swing can still connect failed: " + e);
            }
        }

        private static bool InSession()
        {
            return CoopSession.State == CoopSessionState.Hosting || CoopSession.State == CoopSessionState.Joined;
        }

        /// <summary>
        /// Said once per session, the first time any of the three gates is widened.
        ///
        /// One line rather than one per gate: they fire within a frame of each other, thousands of times
        /// a minute, and which of them fired first says nothing. The counts in <c>coop_status</c> are
        /// where the detail belongs.
        /// </summary>
        private static void Announce()
        {
            if (_reported) return;

            _reported = true;

            MelonLogger.Msg("[Enemy] the world is being kept switched on around a remote player. Drova " +
                "measures what to build, what can be hit and what can hit from the camera, and there is " +
                "one camera - so without this the far end of a session is scenery.");
        }
    }
}
