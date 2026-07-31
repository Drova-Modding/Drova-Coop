using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.AI.Modules;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes the game tick creatures that are near <em>a</em> player rather than near <em>the</em> player.
    ///
    /// **Drova idles an AI more than a thousand units from the player**, and the player it means is one
    /// cached field holding one actor. Everything about that is right for a game with one person in it and
    /// wrong the moment there are two: a client fighting at the far end of the map is fighting creatures
    /// the host has stopped thinking about, so the host - which owns their health - never answers a blow,
    /// and any creature it drives stands still.
    ///
    /// **Only the distance test is touched.** The other gates on the same path are left exactly as the game
    /// wrote them: they turn AI off during loading, teleports and scene fades, and those are conditions a
    /// second player does not change. The one that used to matter here as well - the refusal to tick any AI
    /// while the player is dead - is already answered elsewhere, because a player in a session never
    /// reaches the dead state at all; <see cref="DownedState"/> puts them on the floor instead.
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

        private static int _extended;
        private static bool _reported;

        // Reused so a test that runs for every creature every few frames allocates nothing.
        private static readonly List<Vector2> _playerPositions = new();

        /// <summary>How many times a creature was kept awake for a remote player. For <c>coop_status</c>.</summary>
        internal static int Extended => _extended;

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(ManualGraphUpdater), "IsPlayerInRange",
                typeof(EnemyPerception), nameof(IsPlayerInRangePostfix));
        }

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            _extended = 0;
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
                if (__result) return;

                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;

                // Nobody else is here, so there is no second player to measure from and the game's answer
                // was the whole truth.
                if (RemotePlayers.Count == 0) return;

                Actor actor = __instance._actor;
                if (actor == null) return;

                Vector2 position = actor.GetFeetPosition();

                CollectRemotePlayers();

                foreach (Vector2 player in _playerPositions)
                {
                    if ((player - position).sqrMagnitude > PlayerRange * PlayerRange) continue;

                    __result = true;
                    _extended++;

                    if (!_reported)
                    {
                        _reported = true;
                        MelonLogger.Msg("[Enemy] a creature was kept awake because a remote player is standing " +
                            "near it. Without this the far end of a session is a world that has stopped thinking.");
                    }

                    return;
                }
            }
            catch (Exception e)
            {
                // Swallowed: this runs inside the AI's own update, and an exception let out would take the
                // creature's whole tick with it.
                MelonLogger.Error("[Enemy] deciding whether a creature is near a player failed: " + e);
            }
        }

        /// <summary>
        /// The remote players only. The local one is what the game already measured against, so including
        /// it would be doing the same work twice for the common answer.
        /// </summary>
        private static void CollectRemotePlayers()
        {
            _playerPositions.Clear();

            foreach (KeyValuePair<Messages.PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                Actor actor = body.Value.Body;
                if (actor != null)
                {
                    _playerPositions.Add(actor.GetFeetPosition());
                }
            }
        }
    }
}
