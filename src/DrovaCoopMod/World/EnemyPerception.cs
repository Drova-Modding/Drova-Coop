using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.AI.Graphs;
using Il2CppDrova.AI.Modules;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes the game tick creatures that are near <em>a</em> player rather than near <em>the</em> player.
    ///
    /// Drova idles an AI more than a thousand units from the player, and the player it means is one
    /// cached field holding one actor. Everything about that is right for a game with one person in it and
    /// wrong the moment there are two: a client fighting at the far end of the map is fighting creatures
    /// the host has stopped thinking about, so the host - which owns their health - never answers a blow,
    /// and any creature it drives stands still.
    ///
    /// Two gates, in two systems that do not know about each other. <c>ManualGraphUpdater</c> decides how
    /// often to run a creature's behavior tree at all; <c>IsPlayerInRangeCondition</c> is a node the trees
    /// themselves are built out of, so a creature can be ticking at full rate and still take the branch
    /// that says nobody is here.
    ///
    /// Only the distance test is touched. The other gates on the same path are left exactly as the game
    /// wrote them: they turn AI off during loading, teleports and scene fades, and those are conditions a
    /// second player does not change. The one that used to matter here as well - the refusal to tick any AI
    /// while the player is dead - is already answered elsewhere, because a player in a session never
    /// reaches the dead state at all; <see cref="DownedState"/> puts them on the floor instead.
    ///
    /// What decides whether a creature is a body at all rather than how often it thinks is a different
    /// system again, measured from the camera; see <see cref="CameraReach"/>.
    ///
    /// Host-side in effect rather than in principle: a client's own copies are mostly driven or irrelevant,
    /// but the patch is symmetric because a client is the machine with a player standing next to those
    /// creatures, and its own AI has the same reason to run.
    /// </summary>
    internal static class EnemyPerception
    {
        /// <summary>
        /// The game's own idling distance. Reproduced rather than widened - the point is to measure it
        /// from every player rather than from one.
        /// </summary>
        private const float PlayerRange = 1000f;

        private static int _asked;
        private static int _extended;
        private static int _askedCondition;
        private static int _extendedCondition;
        private static bool _reported;

        // The condition is re-run with a different player in the field it reads, which re-enters this
        // patch. Static rather than per instance because the recursion is synchronous and one deep.
        private static bool _reentering;

        /// <summary>
        /// How many times the game asked whether a creature is near the player.
        ///
        /// Zero means the patch never ran, which on this build is a real possibility rather than a
        /// theoretical one: <c>IsPlayerInRange</c> is private, three lines long and called from eight
        /// places inside its own class, and a patch on something the compiler folded into its callers
        /// applies cleanly and is never invoked. That reads identically to a patch that runs and finds
        /// nobody, which is why both numbers are kept.
        /// </summary>
        internal static int Asked => _asked;

        /// <summary>How many times a creature was kept awake for a remote player. For <c>coop_status</c>.</summary>
        internal static int Extended => _extended;

        /// <summary>How many times a behavior tree asked whether the player is in range. For <c>coop_status</c>.</summary>
        internal static int AskedCondition => _askedCondition;

        /// <summary>How many times that question was answered for a remote player. For <c>coop_status</c>.</summary>
        internal static int ExtendedCondition => _extendedCondition;

        /// <summary>
        /// Applies the patches. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(ManualGraphUpdater), "IsPlayerInRange",
                typeof(EnemyPerception), nameof(IsPlayerInRangePostfix));

            Hooking.TryPostfix(harmony, typeof(IsPlayerInRangeCondition), "OnCheck",
                typeof(EnemyPerception), nameof(OnCheckPostfix));
        }

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            _asked = 0;
            _extended = 0;
            _askedCondition = 0;
            _extendedCondition = 0;
        }

        /// <summary>
        /// Says yes for a creature standing near somebody, when the game only looked at one somebody.
        ///
        /// A postfix that can only turn a no into a yes. The game's own answer is never overruled in the
        /// other direction, so nothing this does can stop a creature the game wanted to tick.
        /// </summary>
        private static void IsPlayerInRangePostfix(ManualGraphUpdater __instance, ref bool __result)
        {
            try
            {
                _asked++;

                if (__result) return;

                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;

                // Nobody else is here, so the game's answer was the whole truth.
                if (RemotePlayers.Count == 0) return;

                Actor actor = __instance._actor;
                if (actor == null) return;

                if (!RemotePlayers.IsAnyWithin(actor.GetFeetPosition(), PlayerRange)) return;

                __result = true;
                _extended++;

                Announce();
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside the AI's own update, and an exception let out would take the
                // creature's whole tick with it.
                MelonLogger.Error("[Enemy] deciding whether a creature is near a player failed: " + e);
            }
        }

        /// <summary>
        /// Asks the behavior tree's own range condition again, once per remote player.
        ///
        /// Re-run rather than reimplemented. The condition is not a distance comparison: it widens by
        /// a serialized multiplier once it has said yes, and widens again while the creature is facing the
        /// player, so a copy of the arithmetic here would answer differently at exactly the ranges where
        /// it matters. Putting another player in the field it reads and letting it decide for itself keeps
        /// the thresholds, the hysteresis and the facing bonus the game's own.
        ///
        /// The field is put back before returning, whatever happens. It is the local player's actor,
        /// assigned once when the world loaded, and a condition left pointing at a puppet would answer
        /// about a body that can be destroyed underneath it - which reads as null, and null is a
        /// permanent no.
        /// </summary>
        private static void OnCheckPostfix(IsPlayerInRangeCondition __instance, ref bool __result)
        {
            if (_reentering) return;

            Actor? restore = null;

            try
            {
                _askedCondition++;

                if (__result) return;

                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (RemotePlayers.Count == 0) return;

                restore = __instance._playerActor;
                if (restore == null) return;

                _reentering = true;

                foreach (KeyValuePair<Messages.PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
                {
                    Actor other = body.Value.Body;
                    if (other == null) continue;

                    __instance._playerActor = other;

                    if (!__instance.OnCheck()) continue;

                    __result = true;
                    _extendedCondition++;

                    Announce();
                    return;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] asking a behavior tree about a remote player failed: " + e);
            }
            finally
            {
                _reentering = false;

                if (restore != null)
                {
                    __instance._playerActor = restore;
                }
            }
        }

        private static void Announce()
        {
            if (_reported) return;

            _reported = true;

            MelonLogger.Msg("[Enemy] a creature was kept awake because a remote player is standing near " +
                "it. Without this the far end of a session is a world that has stopped thinking.");
        }
    }
}
