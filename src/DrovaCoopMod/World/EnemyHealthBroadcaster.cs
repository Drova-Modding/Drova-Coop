using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Broadcasts host-owned creature health, coalesced except for the drop to zero.
    /// </summary>
    internal static class EnemyHealthBroadcaster
    {
        /// <summary>Minimum seconds between sends for one creature.</summary>
        private const float MinIntervalSeconds = 0.1f;

        /// <summary>
        /// Seconds before an unchanged figure is repeated, so a client's own local health logic (a Draugr's revive heal) cannot stand.
        /// </summary>
        private const float RefreshSeconds = 2f;

        private static readonly Dictionary<EnemyNetId, ushort> LastSent = new();
        private static readonly Dictionary<EnemyNetId, float> SinceSent = new();
        private static readonly List<EnemyNetId> Finished = new();

        /// <summary>Sends what changed. Call once per frame, after this frame's damage reports were applied.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Hosting)
            {
                Clear();
                return;
            }

            try
            {
                Finished.Clear();

                foreach (KeyValuePair<EnemyNetId, float> tracked in SinceSent)
                {
                    Finished.Add(tracked.Key);
                }

                foreach (EnemyNetId id in EnemyRegistry.ClaimedIds)
                {
                    Consider(id, unscaledDeltaTime);
                    Finished.Remove(id);
                }

                // Unclaimed creatures stop being tracked, so timers do not accumulate.
                foreach (EnemyNetId id in Finished)
                {
                    LastSent.Remove(id);
                    SinceSent.Remove(id);
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
            LastSent.Clear();
            SinceSent.Clear();
        }

        private static void Consider(EnemyNetId id, float unscaledDeltaTime)
        {
            // A leased fight's health lives on the machine fighting it. The held copy here is full and invincible.
            if (EnemyEngagements.TryGetLease(id, out _)) return;

            if (!EnemyRegistry.TryResolveOnHost(id, out Actor actor)) return;

            Health health = actor.GetHealth();
            if (health == null) return;

            // Deaths from relayed blows are invisible to the health funnel, so the true state is announced here.
            // As a reconcile, not a kill, since this also visits corpses already dead in this save.
            if (health.IsDead && EnemyRegistry.TryGetSharedGuid(id, out string deadGuid)
                && !EnemyRegistry.IsKnownDead(deadGuid))
            {
                EnemyRegistry.RecordDead(deadGuid);
                CoopSession.ReportEnemyDeath(deadGuid, isReconcile: true);
            }

            ushort current = (ushort)Math.Clamp(health.CurHealth, 0, ushort.MaxValue);

            SinceSent.TryGetValue(id, out float since);
            since += unscaledDeltaTime;
            SinceSent[id] = since;

            bool isKnown = LastSent.TryGetValue(id, out ushort last);
            if (isKnown && last == current && (current == 0 || since < RefreshSeconds)) return;

            // Death goes out immediately, since nothing will supersede it.
            bool isDeath = current == 0;

            if (!isDeath && isKnown && since < MinIntervalSeconds) return;

            LastSent[id] = current;
            SinceSent[id] = 0f;

            CoopSession.BroadcastEnemyHealth(id, current, (ushort)Math.Clamp(health.MaxHealth, 0, ushort.MaxValue));
        }
    }
}
