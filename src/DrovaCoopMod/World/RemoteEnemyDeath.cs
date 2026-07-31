using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Kills the creatures other players killed.
    ///
    /// The far half of <see cref="EnemyDeathWatch"/>, and deliberately the whole of what it does: the
    /// health that got the creature there is not replicated, so what arrives is the conclusion rather than
    /// the fight. The other player's wolf may have been at full health on this screen a moment ago, and it
    /// will drop anyway. That is the honest shape of a first slice - the disagreement it removes is
    /// permanent, the one it leaves is not.
    ///
    /// Applied through <c>SetHealth</c>, which is normally the wrong call. Everywhere else in this mod
    /// health goes through <c>ChangeHealth</c>, because that is where the damage blockers live and one of
    /// them is what turns a killing blow into a knockout. Here the blockers are exactly what must not run:
    /// the host has already decided this creature is dead, and a local blocker that saved it would produce
    /// the one state the game cannot leave - alive here, dead there, forever. <c>SetHealth</c> ignores
    /// invincibility and the death flag, clamps, and still drives the game's whole death chain, so the
    /// animation, the ragdoll, the experience and the loot all happen locally as though this machine had
    /// landed the blow.
    /// </summary>
    internal static class RemoteEnemyDeath
    {
        /// <summary>
        /// Deaths whose body this machine has not got yet.
        ///
        /// Never dropped, and this is the common case rather than an edge. Each machine loads the
        /// world around its own player, nearest first, so a creature can be dead on one screen well before
        /// the other has streamed the chunk it stands in. Retried every frame until the body appears.
        /// </summary>
        private static readonly List<EnemyDeathMessage> _waiting = new();

        /// <summary>
        /// How many unresolved deaths are held.
        ///
        /// Bounded, unlike the queue that feeds it. The session's own cap governs messages that have
        /// arrived and not been taken; this list is what they become afterwards, and a death whose body
        /// never appears stays in it for the rest of the session. One that cannot be resolved is worth
        /// keeping - the chunk it lives in may simply not have streamed yet - but only up to the point
        /// where the list itself is the problem.
        /// </summary>
        private const int MaxWaiting = 512;

        /// <summary>
        /// How often the unresolved ones are tried again.
        ///
        /// Not every frame. Each attempt is a dictionary lookup across the interop boundary per waiting
        /// death, and what it is waiting for - a chunk of the world to stream in around this player - does
        /// not happen at frame rate. Four times a second is far faster than a body can appear.
        /// </summary>
        private const float RetrySeconds = 0.25f;

        private static float _sinceRetry;
        private static int _refused;

        /// <summary>
        /// Applies everything that has arrived. Call once per frame from a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
            {
                Clear();
                return;
            }

            while (CoopSession.TryTakeEnemyDeath(out EnemyDeathMessage message))
            {
                if (_waiting.Count >= MaxWaiting)
                {
                    MelonLogger.Warning($"[Enemy] more than {MaxWaiting} reported deaths are waiting for a body " +
                        $"that has not appeared; '{message.ActorGuid}' was dropped and that creature will stay " +
                        "alive here. This many usually means guids are arriving that this world cannot resolve.");
                    continue;
                }

                _waiting.Add(message);
            }

            if (_waiting.Count == 0) return;

            _sinceRetry += unscaledDeltaTime;
            if (_sinceRetry < RetrySeconds) return;

            _sinceRetry = 0f;

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            // Backwards, so removing the one that landed does not skip the next.
            for (int index = _waiting.Count - 1; index >= 0; index--)
            {
                if (Apply(entities, _waiting[index]))
                {
                    _waiting.RemoveAt(index);
                }
            }
        }

        /// <summary>
        /// Forgets what is waiting. Called when the world goes away, because the guids belong to a scene
        /// that no longer exists.
        /// </summary>
        internal static void Clear()
        {
            _waiting.Clear();
            _sinceRetry = 0f;
        }

        /// <summary>How many deaths are waiting for a body to arrive. For <c>coop_status</c>.</summary>
        internal static int Pending => _waiting.Count;

        /// <summary>
        /// How many reported deaths this world refused because it protects the actor. For
        /// <c>coop_status</c>.
        /// </summary>
        internal static int Refused => _refused;

        /// <summary>
        /// Puts one reported death onto the creature it names.
        /// </summary>
        /// <returns>True when it landed or can never land; false to keep waiting for the body.</returns>
        private static bool Apply(EntityGameHandler entities, in EnemyDeathMessage message)
        {
            try
            {
                Actor actor = entities.GetActorFromGuid(message.ActorGuid);
                if (actor == null) return false;

                Health health = actor.GetHealth();

                // No health is not a body that has not arrived; it is one that will never be killable.
                // Kept out of the waiting list rather than retried forever.
                if (health == null) return true;

                if (health.IsDead) return true;

                // This machine's own world gets a veto, because the write about to happen has none.
                // SetHealth stores the figure directly and never consults the damage blockers, and a
                // blocker is exactly what keeps a great many of Drova's people alive: HealthBhvr_ReviveBhvr
                // installs a ReviveBlocker whose whole job is to turn a killing blow into two health and a
                // knockout. Whether it is installed is decided by quest state, which this mod does not
                // replicate - so a player further through the story can kill somebody their friend's world
                // still protects, and killing them here would be permanent, since _isDead latches and
                // nothing in the game ever clears it.
                //
                // Refused rather than deferred. The two worlds genuinely disagree about whether this
                // person can die, and the recoverable answer is the one that leaves them standing.
                if (health.HasDamageBlockerActive)
                {
                    _refused++;

                    if (_refused == 1)
                    {
                        MelonLogger.Warning("[Enemy] refused a reported death: this world protects that actor " +
                            "from dying, which usually means the two players are at different points in the " +
                            "story. It stays alive here. Count is in coop_status.");
                    }

                    return true;
                }

                bool isReconcile = message.IsReconcile;

                // The game's own switch for the difference between dying and having died. A corpse that
                // streams in beside a player must not play its death animation, award its experience or
                // cast whatever some creatures cast as they fall - all of which hang off the same event
                // this write is about to raise. It is set on the args rather than on the change because
                // the args are what every listener receives.
                Action<HealthChangeArgs> markLoaded = new(args =>
                {
                    if (args != null && isReconcile)
                    {
                        args.SetIsLoadingArg(true);
                    }
                });

                using (RemoteApply<EnemyHealthScope>.Enter())
                {
                    health.SetHealth(0, null, markLoaded);
                }

                return true;
            }
            catch (Exception e)
            {
                // Swallowed, and the death is dropped rather than retried: an exception here repeats every
                // frame for the rest of the session, and a log filling at frame rate is worse than one
                // creature left standing.
                MelonLogger.Error($"[Enemy] killing '{message.ActorGuid}' failed: " + e);
                return true;
            }
        }
    }
}
