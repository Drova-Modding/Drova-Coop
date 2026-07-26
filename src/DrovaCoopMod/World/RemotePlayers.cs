using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps a body in the world for every other player in the session, and moves it.
    ///
    /// The roster is the authority on who exists and the state stream is the authority on where they are;
    /// this reconciles both against what is actually in the scene. Reconciling every frame rather than
    /// reacting to join and leave events is deliberate - it is a handful of dictionary lookups, and it
    /// cannot drift out of step with the roster the way a missed event would.
    /// </summary>
    internal static class RemotePlayers
    {
        private static readonly Dictionary<PlayerId, RemotePlayer> _bodies = new();
        private static readonly List<PlayerId> _departed = new();

        /// <summary>
        /// How many bodies are currently in the world.
        /// </summary>
        internal static int Count => _bodies.Count;

        /// <summary>
        /// The bodies in the world, by the player they belong to. For anything that has to draw them.
        /// </summary>
        internal static IReadOnlyDictionary<PlayerId, RemotePlayer> Bodies => _bodies;

        /// <summary>
        /// Spawns, despawns and drives remote bodies. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Clear();
                return;
            }

            // A body cannot be built before there is a world to put it in, and PlayerAccess is the
            // readiness signal for that: it reports a player only once the world is up and the actor is
            // initialized.
            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer)) return;

            SyncRoster(localPlayer);
            ApplyStates(unscaledDeltaTime);
        }

        /// <summary>
        /// Removes every body. Called when a session ends, and when the world goes away underneath one.
        /// </summary>
        internal static void Clear()
        {
            if (_bodies.Count == 0) return;

            foreach (RemotePlayer body in _bodies.Values)
            {
                PuppetFactory.Destroy(body.Body);
            }

            _bodies.Clear();
            MelonLogger.Msg("[Puppet] all remote bodies removed.");
        }

        /// <summary>
        /// Adds a body for anyone in the roster who has none, and removes bodies for anyone who left.
        /// </summary>
        private static void SyncRoster(Actor localPlayer)
        {
            foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
            {
                if (player.Key == CoopSession.LocalPlayerId) continue;
                if (_bodies.ContainsKey(player.Key)) continue;

                // Spawned on top of the local player rather than at the origin: the remote player's real
                // position arrives a fraction of a second later, and a body that appears across the map
                // and slides in is worse than one that appears close and corrects.
                if (!PuppetFactory.TryCreate($"DrovaCoop.Remote.{player.Key.Value}", localPlayer.transform.position, out Actor? body) || body == null)
                {
                    continue;
                }

                _bodies[player.Key] = new RemotePlayer(player.Key, body);
                MelonLogger.Msg($"[Puppet] body created for {player.Key} '{player.Value}'.");
            }

            _departed.Clear();
            foreach (PlayerId present in _bodies.Keys)
            {
                if (!CoopSession.Players.ContainsKey(present))
                {
                    _departed.Add(present);
                }
            }

            foreach (PlayerId gone in _departed)
            {
                PuppetFactory.Destroy(_bodies[gone].Body);
                _bodies.Remove(gone);
                MelonLogger.Msg($"[Puppet] body removed for {gone}.");
            }
        }

        /// <summary>
        /// Feeds each body whatever arrived for it and lets the game move it.
        /// </summary>
        private static void ApplyStates(float unscaledDeltaTime)
        {
            foreach (KeyValuePair<PlayerId, RemotePlayer> body in _bodies)
            {
                if (CoopSession.LatestStates.TryGetValue(body.Key, out PlayerStateMessage state))
                {
                    body.Value.Record(state);
                }

                // Applied every frame, not only on arrival: intent is a standing instruction, and the
                // movement module expects to be told the direction each tick the way the input layer
                // tells it.
                body.Value.Apply(unscaledDeltaTime);
            }
        }
    }
}
