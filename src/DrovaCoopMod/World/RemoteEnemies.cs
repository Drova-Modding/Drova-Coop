using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// The creatures this client shows under the host's control.
    ///
    /// Keeps one driver per bound creature and reconciles that set against what the registry says every
    /// frame, rather than reacting to binds and releases as they arrive. The same reasoning as for the
    /// player bodies: a body can appear or be destroyed without anything telling this mod - a chunk
    /// unloading here does exactly that - and comparing what is against what should be covers every route
    /// without any of them needing to know about this.
    /// </summary>
    internal static class RemoteEnemies
    {
        private static readonly Dictionary<EnemyNetId, RemoteEnemy> _driven = new();
        private static readonly List<EnemyNetId> _gone = new();

        private static int _desynced;
        private static bool _reportedDesync;

        /// <summary>How many creatures this client is currently being driven. For <c>coop_status</c>.</summary>
        internal static int Count => _driven.Count;

        /// <summary>
        /// How many updates were refused because this world has already killed the creature they describe.
        /// For <c>coop_status</c>. A non-zero count means the two saves disagree about what is dead.
        /// </summary>
        internal static int Desynced => _desynced;

        /// <summary>
        /// Applies whatever has arrived and keeps the driven set honest. Call once per frame from a loaded
        /// world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Joined)
            {
                Clear();
                return;
            }

            try
            {
                while (CoopSession.TryTakeEnemySnapshot(out EnemySnapshotMessage snapshot))
                {
                    Apply(snapshot);
                }

                DropWhatIsGone();

                foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in _driven)
                {
                    driven.Value.Tick(unscaledDeltaTime);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] driving the host's creatures failed: " + e);
            }
        }

        /// <summary>
        /// Hands every creature back to its own AI and forgets them.
        ///
        /// The hand-back matters more than the forgetting. A creature left with its AI switched off is
        /// inert forever, and unlike write-protection that is visible immediately - a camp standing
        /// perfectly still while the player walks through it.
        /// </summary>
        internal static void Clear()
        {
            foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in _driven)
            {
                driven.Value.HandBack();
            }

            _driven.Clear();
            _desynced = 0;
            _reportedDesync = false;
        }

        /// <summary>
        /// Whether this machine has already killed the creature an update describes.
        ///
        /// Asked of the health rather than of anything this mod remembers, because the answer usually
        /// comes from the player's own save file rather than from anything that happened in the session.
        /// </summary>
        private static bool IsLocallyDead(Actor body)
        {
            Health health = body.GetHealth();

            return health != null && health.IsDead;
        }

        private static void Apply(in EnemySnapshotMessage snapshot)
        {
            if (!snapshot.IsPlausible()) return;

            Vector2 origin = new(snapshot.OriginX, snapshot.OriginY);

            for (int index = 0; index < snapshot.Count; index++)
            {
                EnemySnapshotMessage.Record record = snapshot.Records[index];

                if (!EnemyRegistry.TryGetBody(record.NetId, out Actor body)) continue;

                // A body that is still starting its modules. Write-protection is installed the moment it is
                // bound, which is what matters; driving it has to wait, because taking its AI away and
                // setting a direction both go through movement state that does not exist yet - see
                // EnemyRegistry.IsReady.
                if (!EnemyRegistry.IsReady(body)) continue;

                // A creature the host owns but does not drive keeps its own behavior here. Only its
                // health is arbitrated, and taking its AI away would leave it standing still on the one
                // screen that has a player next to it.
                if (!EnemyRegistry.IsDriven(record.NetId)) continue;

                // A corpse is never driven around, whatever the host says. The two players' saves need
                // not agree about who is already dead: this player may have cleared a camp in their own game
                // before ever hosting or joining, and the game restores those creatures as corpses while the
                // other player's world still has them standing.
                //
                // <see cref="EnemyRebirth"/> is what resolves that, by having the game build the creature a
                // living body. This is the net underneath it: while a rebuild is in flight, and permanently
                // for anything that could not be rebuilt, the body here is a death frame with its animator
                // parked and its sprites greyed. A corpse left lying is a world that is merely out of date;
                // a corpse sliding across the ground under the host's control is the game visibly broken.
                if (IsLocallyDead(body))
                {
                    // Agreement, not divergence, and it was being counted as the latter. A creature the
                    // host has also killed is dead in both worlds, which is the state everything here is
                    // working towards - but the host keeps sending updates for it, so every one arrived
                    // looking exactly like a corpse the two players disagree about.
                    //
                    // Otherwise counted only once the rebuild has been given up on: a corpse waiting for its
                    // new body is refused for a second or two by design, and counting those made the number
                    // say "permanently wrong" about the case that fixes itself.
                    bool known = EnemyRegistry.TryGetBoundGuid(record.NetId, out string guid);

                    if (known && EnemyRegistry.IsKnownDead(guid)) continue;

                    if (!known || !EnemyRebirth.IsWorkingOn(guid))
                    {
                        _desynced++;

                        if (!_reportedDesync)
                        {
                            _reportedDesync = true;
                            MelonLogger.Warning("[Enemy] the other player's world still has a creature this " +
                                "one has already killed, and it could not be given a new body. It stays a " +
                                "corpse here rather than being driven around - see coop_status.");
                        }
                    }

                    continue;
                }

                // A body the game has swapped out for a placeholder is still resolvable by guid but is no
                // longer in the scene, so driving it produces a creature that acts without being drawn.
                if (!body.gameObject.activeInHierarchy) continue;

                if (!_driven.TryGetValue(record.NetId, out RemoteEnemy? driver))
                {
                    driver = new RemoteEnemy(body);
                    _driven[record.NetId] = driver;
                }
                else if (driver.Body == null || driver.Body.Pointer != body.Pointer)
                {
                    // The body was replaced under us - the chunk unloaded and streamed back in. The old
                    // driver refers to an object that no longer exists, so it is dropped rather than
                    // handed back to an AI it cannot reach.
                    driver = new RemoteEnemy(body);
                    _driven[record.NetId] = driver;
                }

                driver.Apply(record, origin);
            }
        }

        /// <summary>
        /// Gives back anything the host has released or whose body has gone.
        /// </summary>
        private static void DropWhatIsGone()
        {
            _gone.Clear();

            foreach (KeyValuePair<EnemyNetId, RemoteEnemy> driven in _driven)
            {
                if (driven.Value.Body == null)
                {
                    _gone.Add(driven.Key);
                    continue;
                }

                if (!EnemyRegistry.TryGetBody(driven.Key, out _) || !EnemyRegistry.IsDriven(driven.Key))
                {
                    _gone.Add(driven.Key);
                }
            }

            foreach (EnemyNetId id in _gone)
            {
                if (_driven.TryGetValue(id, out RemoteEnemy? driver))
                {
                    driver.HandBack();
                    _driven.Remove(id);
                }
            }
        }
    }
}
