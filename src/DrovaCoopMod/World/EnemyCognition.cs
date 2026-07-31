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
    /// **Everything an enemy is allowed to fight has to be perceived first.** The target chain ends in
    /// <c>TargetIsInCombat</c>, which keeps a candidate only when the creature already has it in combat or
    /// <c>IsPerceivingActor</c> says yes; anything else is dropped before distance or threat is ever
    /// consulted. Perception in turn is fed by <c>CognitionOctreeManager</c>, and that is where a game
    /// written for one player says so out loud:
    /// <code>
    /// Vector2 feetPosition = _playerActor.GetFeetPosition();
    /// ...
    /// Valid = (Vector2.Distance(feetPosition, position) &lt; _cognitionActivePlayerInRange)
    /// </code>
    /// Only cognition objects within a few hundred units of <em>the</em> player are marked valid, and the
    /// invalid ones are never returned to anybody. A creature standing next to a teammate at the far end of
    /// the map is therefore handed an empty world: it perceives nothing, so it can target nothing, and it
    /// stands there while somebody hits it.
    ///
    /// That is also why walking the host over made the fight start - and start against the host. Their
    /// arrival is what turns the cull off for that stretch of ground, so both bodies become perceivable at
    /// once, and the choice between them is then made on threat, which is a separate problem with its own
    /// answer in <see cref="EnemyThreat"/>.
    ///
    /// **A postfix that can only add.** The game's own answer is left in the list exactly as it built it;
    /// what this puts back is the objects its player-distance test discarded and a second player makes
    /// relevant again. Nothing here can hide something the game wanted to return.
    /// </summary>
    internal static class EnemyCognition
    {
        // Reused: this runs for every creature collecting perception, several times a second each.
        private static readonly List<Vector2> _playerPositions = new();

        private static int _restored;
        private static bool _reported;

        /// <summary>
        /// How many times something was made perceivable because a remote player is near it. For
        /// <c>coop_status</c>.
        /// </summary>
        internal static int Restored => _restored;

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
            _restored = 0;
        }

        private static void GetCognitionObjectsPostfix(
            CognitionOctreeManager __instance, Vector2 position, float radius,
            FastRemoveList<ICognitionObject> __result)
        {
            try
            {
                if (__result == null) return;

                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;

                // Nobody else is here, so the game measured against every player there is.
                if (RemotePlayers.Count == 0) return;

                FastRemoveList<ICognitionObject> known = __instance._activeCognitions;
                if (known == null || known.Count == 0) return;

                CollectRemotePlayers();
                if (_playerPositions.Count == 0) return;

                // The game's own validity radius rather than a number of this mod's choosing. It is not a
                // constant: every collector raises it to the widest range asked for last frame, and reusing
                // it is what keeps a second player perceived on exactly the terms the first one is.
                float keep = __instance._cognitionActivePlayerInRange;

                // Nothing a remote player could rescue can be within reach of this query, so the walk below
                // would find nothing. Worth one distance test per call to skip, because the common case in a
                // session is two players nowhere near each other.
                if (!IsAnyPlayerWithin(position, radius + keep)) return;

                for (int index = 0; index < known.Count; index++)
                {
                    ICognitionObject candidate = known[index];
                    if (candidate == null) continue;

                    // A destroyed actor reports float.MaxValue for both axes, so it fails the range test
                    // below rather than needing a check of its own.
                    Vector2 where = candidate.Position;

                    if ((where - position).sqrMagnitude > radius * radius) continue;
                    if (!IsAnyPlayerWithin(where, keep)) continue;

                    // Add answers false for something the game already returned, which is what makes
                    // repeating its work harmless and the count below mean what it says.
                    if (!__result.Add(candidate)) continue;

                    _restored++;

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

        /// <summary>
        /// The remote players only. The local one is what the game already measured against, so including
        /// it would be re-adding what is already in the list.
        /// </summary>
        private static void CollectRemotePlayers()
        {
            _playerPositions.Clear();

            foreach (KeyValuePair<Messages.PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                Il2CppDrova.Actor actor = body.Value.Body;
                if (actor != null)
                {
                    _playerPositions.Add(actor.GetFeetPosition());
                }
            }
        }

        private static bool IsAnyPlayerWithin(Vector2 point, float range)
        {
            float squared = range * range;

            foreach (Vector2 player in _playerPositions)
            {
                if ((player - point).sqrMagnitude <= squared) return true;
            }

            return false;
        }
    }
}
