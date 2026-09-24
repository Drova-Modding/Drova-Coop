using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Tells everyone what the creatures the host owns are down to.
    ///
    /// Coalesced, except for the one that cannot be. A fight produces health changes far faster than
    /// anybody needs to see them, and each one is a reliable message to every client - so they are held
    /// and sent at a rate a health bar can actually show. The change to zero is exempt: every other figure
    /// is superseded by the next one, and that one never is.
    /// </summary>
    internal static class EnemyHealthBroadcaster
    {
        /// <summary>
        /// How often one creature's health is sent at most. Ten times a second is faster than a bar can be
        /// read and slow enough that a fight with a dozen creatures does not flood the link.
        /// </summary>
        private const float MinIntervalSeconds = 0.1f;

        private static readonly Dictionary<EnemyNetId, ushort> _lastSent = new();
        private static readonly Dictionary<EnemyNetId, float> _sinceSent = new();
        private static readonly List<EnemyNetId> _finished = new();

        /// <summary>
        /// Sends what has changed. Call once per frame from a loaded world, after the host has applied the
        /// blows this frame's reports carried - so what goes out is the health those blows produced rather
        /// than the health before them.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting)
            {
                Clear();
                return;
            }

            try
            {
                _finished.Clear();

                foreach (KeyValuePair<EnemyNetId, float> tracked in _sinceSent)
                {
                    _finished.Add(tracked.Key);
                }

                // Everything currently claimed, whether or not it has been sent before.
                foreach (EnemyNetId id in CoopSession.ClaimedEnemies)
                {
                    Consider(id, unscaledDeltaTime);
                    _finished.Remove(id);
                }

                // Anything no longer claimed stops being tracked, so a long session does not accumulate a
                // timer per creature it once owned.
                foreach (EnemyNetId id in _finished)
                {
                    _lastSent.Remove(id);
                    _sinceSent.Remove(id);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] broadcasting creature health failed: " + e);
            }
        }

        /// <summary>Forgets everything. Called when the world goes away or a session ends.</summary>
        internal static void Clear()
        {
            _lastSent.Clear();
            _sinceSent.Clear();
        }

        private static void Consider(EnemyNetId id, float unscaledDeltaTime)
        {
            // A leased brawl's health lives on the machine fighting it. The held copy here is full and
            // invincible, and announcing that figure would heal the fight back up mid-swing.
            if (EnemyEngagements.TryGetLease(id, out _)) return;

            if (!EnemyRegistry.TryResolveOnHost(id, out Actor actor)) return;

            Health health = actor.GetHealth();
            if (health == null) return;

            // Said from what is true, rather than only from what was seen to happen. Every death that
            // crosses otherwise crosses because the health funnel watched it happen, and that funnel is
            // deliberately deaf while this machine is writing from the wire - so a creature killed by a
            // teammate's relayed blow could die here and be announced by nobody at all.
            //
            // Reported as a reconcile rather than as a kill, and that distinction is load-bearing: this
            // walk also visits corpses that were already dead in this world's save when they were
            // claimed, and announcing those as fresh kills would replay their death - animation, loot,
            // payout - on the far machine. RecordDead is idempotent and ReportEnemyDeath deduplicates,
            // so a creature that is already settled costs a dictionary lookup.
            if (health.IsDead && EnemyRegistry.TryGetSharedGuid(id, out string deadGuid)
                && !EnemyRegistry.IsKnownDead(deadGuid))
            {
                EnemyRegistry.RecordDead(deadGuid);
                CoopSession.ReportEnemyDeath(deadGuid, isReconcile: true);
            }

            ushort current = (ushort)Math.Clamp(health.CurHealth, 0, ushort.MaxValue);

            _sinceSent.TryGetValue(id, out float since);
            since += unscaledDeltaTime;
            _sinceSent[id] = since;

            bool isKnown = _lastSent.TryGetValue(id, out ushort last);
            if (isKnown && last == current) return;

            // Death goes out immediately, whatever the clock says. Every other figure is corrected by the
            // next one; this one is the last thing anybody will hear about this creature.
            bool isDeath = current == 0;

            if (!isDeath && isKnown && since < MinIntervalSeconds) return;

            _lastSent[id] = current;
            _sinceSent[id] = 0f;

            CoopSession.BroadcastEnemyHealth(id, current, (ushort)Math.Clamp(health.MaxHealth, 0, ushort.MaxValue));
        }
    }
}
