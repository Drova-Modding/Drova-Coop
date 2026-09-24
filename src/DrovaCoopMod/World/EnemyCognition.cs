using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova.Cognition;
using Il2CppDrova.Utilities.LazyLoading;
using Il2CppGilzoide.UpdateManager.Internal;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Lets a creature notice a remote player standing next to it.
    ///
    /// Noticing and targeting are two systems, and this is the noticing one. A behavior tree asks
    /// <c>CognitionCondition</c> whether there is anything worth reacting to, and that reads the list the
    /// creature has accumulated - fed by its collector, fed in turn by <c>CognitionOctreeManager</c>, which
    /// is where a game written for one player says so out loud:
    /// <code>
    /// Vector2 feetPosition = _playerActor.GetFeetPosition();
    /// ...
    /// Valid = (Vector2.Distance(feetPosition, position) &lt; _cognitionActivePlayerInRange)
    /// </code>
    /// Only cognition objects within a few hundred units of <em>the</em> player are marked valid, and the
    /// invalid ones are never returned to anybody. A creature standing next to a teammate at the far end of
    /// the map therefore reacts to nothing, whatever is happening in front of it.
    ///
    /// It is not what gates the target chain, and it never was. <c>TargetIsInCombat</c> calls
    /// <c>IsPerceivingActor</c>, which goes straight to <c>CheckRequirements</c> on each of the creature's
    /// cognition behaviors - live cone, radius and line-of-sight arithmetic against the actor it is handed.
    /// Nothing on that path reads the list this widens. The perception term there is also conditional on
    /// the creature already being in combat, so it cannot be what stops a fight starting. This file is
    /// worth having for what it actually does; it is not the answer to "the enemy will not fight me".
    ///
    /// A postfix that can only add. The game's own answer is left in the list exactly as it built it;
    /// what this puts back is the objects its player-distance test discarded and a second player makes
    /// relevant again. Nothing here can hide something the game wanted to return.
    ///
    /// A body has to be registered with the manager before any of this can reach it, and a remote player's
    /// is registered by hand - see <see cref="PuppetFactory.KeepNoticeable"/> for why that cannot be left
    /// to the game.
    /// </summary>
    internal static class EnemyCognition
    {
        private static bool _reported;

        /// <summary>
        /// How many times something was made perceivable because a remote player is near it. For
        /// <c>coop_status</c>.
        /// </summary>
        internal static int Restored { get; private set; }

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The public collection method rather than the private RebuildTree that holds the actual test.
            // This one is reached through the ICognitionManager interface, so it exists as a real call and
            // cannot have been folded into its caller; RebuildTree is private, called from exactly one
            // place, and a patch on something inlined applies cleanly and then never runs.
            Hooking.TryPostfix(harmony, typeof(CognitionOctreeManager), "GetCognitionObjects",
                typeof(EnemyCognition), nameof(GetCognitionObjectsPostfix));
        }

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            Restored = 0;
        }

        private static void GetCognitionObjectsPostfix(
            CognitionOctreeManager __instance, Vector2 position, float radius,
            FastRemoveList<ICognitionObject> __result)
        {
            try
            {
                if (__result == null) return;

                if (!CoopSession.IsLive) return;

                // Nobody else is here, so the game measured against every player there is.
                if (RemotePlayers.Count == 0) return;

                FastRemoveList<ICognitionObject> known = __instance._activeCognitions;
                if (known == null || known.Count == 0) return;

                // The game's own validity radius rather than a number of this mod's choosing. It is not a
                // constant: every collector raises it to the widest range asked for last frame, and reusing
                // it is what keeps a second player perceived on exactly the terms the first one is.
                float keep = __instance._cognitionActivePlayerInRange;

                // Nothing a remote player could rescue can be within reach of this query, so the walk below
                // would find nothing. Worth one distance test per call to skip, because the common case in a
                // session is two players nowhere near each other.
                if (!RemotePlayers.IsAnyWithin(position, radius + keep)) return;

                for (int index = 0; index < known.Count; index++)
                {
                    ICognitionObject candidate = known[index];
                    if (candidate == null) continue;

                    // A destroyed actor reports float.MaxValue for both axes, so it fails the range test
                    // below rather than needing a check of its own.
                    Vector2 where = candidate.Position;

                    if ((where - position).sqrMagnitude > radius * radius) continue;
                    if (!RemotePlayers.IsAnyWithin(where, keep)) continue;

                    // Add answers false for something the game already returned, which is what makes
                    // repeating its work harmless and the count below mean what it says.
                    if (!__result.Add(candidate)) continue;

                    Restored++;

                    if (!_reported)
                    {
                        _reported = true;
                        MelonLogger.Msg("[Enemy] a creature was allowed to notice something because a remote " +
                            "player is standing near it. Without this the far end of a session is a world " +
                            "where nothing can be fought.");
                    }
                }
            }
            catch (Exception e)
            {
                // Swallowed: this sits on the perception path of every creature in the world, and an
                // exception let out would take that creature's whole tick with it.
                MelonLogger.Error("[Enemy] widening what a creature can notice failed: " + e);
            }
        }
    }
}
