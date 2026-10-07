using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.ABI;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// A client's side of joinable scripted brawls: the local NPC copy accepts this player's swings in the host's brawl,
    /// and the host's relayed blows land in this player's own leased brawl.
    /// </summary>
    internal static class TeamBrawls
    {
        /// <summary>Three missed keepalives, the same clock the host runs for client engagements.</summary>
        private const float ExpirySeconds = 15f;

        private sealed class Mark
        {
            public float SinceHeard;
            public bool Hostile;
        }

        private static readonly Dictionary<EnemyNetId, Mark> HostBrawls = new();

        /// <summary>How many host brawls this player was invited into. For <c>coop_status</c>.</summary>
        internal static int Joined { get; private set; }

        /// <summary>Whether the host is in a scripted brawl right now, lingering epilogue included.</summary>
        internal static bool AnyHostBrawl => HostBrawls.Count > 0;

        /// <summary>How many relayed teammate blows landed in this player's own brawl. For <c>coop_status</c>.</summary>
        internal static int Assists { get; private set; }

        /// <summary>Forgets everything and takes the hostility marks back. Called when a session ends.</summary>
        internal static void Reset()
        {
            foreach (KeyValuePair<EnemyNetId, Mark> entry in HostBrawls)
            {
                Release(entry.Key, entry.Value);
            }

            HostBrawls.Clear();
            Joined = 0;
            Assists = 0;
        }

        /// <summary>Applies what the host announced and relayed. Call once per frame from a loaded world.</summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Joined)
            {
                if (HostBrawls.Count > 0) Reset();
                return;
            }

            try
            {
                while (CoopSession.TryTakeHostEngage(out EnemyEngageMessage engage))
                {
                    Apply(engage);
                }

                while (CoopSession.TryTakeLeasedDamage(out EnemyDamageMessage blow))
                {
                    ApplyAssist(blow);
                }

                Expire(unscaledDeltaTime);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] following the host's brawls failed: " + e);
            }
        }

        private static void Apply(in EnemyEngageMessage message)
        {
            if (!message.IsPlausible()) return;

            bool known = HostBrawls.TryGetValue(message.NetId, out Mark? mark);

            if (message.Mode != EnemyEngageMessage.ModeBrawl)
            {
                if (!known) return;

                Release(message.NetId, mark!);
                HostBrawls.Remove(message.NetId);
                return;
            }

            if (!known)
            {
                mark = new Mark();
                HostBrawls[message.NetId] = mark;
            }

            mark!.SinceHeard = 0f;
            Invite(message.NetId, mark);
        }

        /// <summary>Makes the local copy accept this player's swings. Retried until the body has streamed in.</summary>
        private static void Invite(EnemyNetId id, Mark mark)
        {
            if (mark.Hostile) return;

            if (!EnemyRegistry.BoundBodies.TryGetValue(id, out Actor? body) || body == null) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            string? guid = player.GetGuidComponent()?.GuidString;
            if (guid == null) return;

            EnemyThreat.Aggro(body, player, guid);
            mark.Hostile = true;
            Joined++;

            MelonLogger.Msg("[Engage] the host is brawling this NPC; this player's blows now count " +
                "toward that fight.");
        }

        private static void Release(EnemyNetId id, Mark mark)
        {
            if (!mark.Hostile) return;

            try
            {
                if (!EnemyRegistry.BoundBodies.TryGetValue(id, out Actor? body) || body == null) return;

                PlayerAccess.TryGetPlayer(out Actor player);
                string guid = player != null ? player.GetGuidComponent()?.GuidString ?? string.Empty : string.Empty;

                // Pardons every body, including the host player's relayed hostilities from the same brawl.
                EnemyThreat.Pardon(body, guid);
                mark.Hostile = false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] standing down from the host's brawl failed: " + e);
            }
        }

        /// <summary>
        /// Lands a teammate's relayed blow in this player's own live brawl. No guard on purpose,
        /// so the brawl's own knockout clamp answers a killing blow.
        /// </summary>
        private static void ApplyAssist(in EnemyDamageMessage report)
        {
            if (!EngageWatch.IsBrawling(report.NetId)) return;
            if (!EnemyRegistry.BoundBodies.TryGetValue(report.NetId, out Actor? body) || body == null) return;

            Health health = body.GetHealth();
            if (health == null || health.IsDead) return;

            if (body.CustomStats == null || !body.CustomStats.IsInBrawl) return;

            if (!RemoteEnemyHealth.TryResolveType(report.DamageType, out DamageStatDesc type)) return;

            HealthChange change = new(null, new HealthChangeValue(report.Amount, type));

            if (report.AppliesBleed != 0)
            {
                change.SetAppliesBleed(true);
            }

            if (report.PoiseDamage > 0f)
            {
                change.SetPotentialPoiseDmg(report.PoiseDamage);
            }

            // The blow scope: a death this causes is this machine's news, under the health scope it would be taken for an echo.
            using (RemoteApply<EnemyBlowScope>.Enter())
            {
                health.ChangeHealth(change);
            }

            Assists++;
            ArenaTeammates.TryJoin(body, RemoteEnemyHealth.AttackerBody(report.Attacker));

            if (Assists == 1)
            {
                MelonLogger.Msg("[Engage] a teammate's blow was carried into this player's brawl.");
            }
        }

        private static void Expire(float unscaledDeltaTime)
        {
            if (HostBrawls.Count == 0) return;

            foreach (KeyValuePair<EnemyNetId, Mark> entry in HostBrawls)
            {
                entry.Value.SinceHeard += unscaledDeltaTime;

                // Retried while the mark lives, so a late-streamed body still joins.
                Invite(entry.Key, entry.Value);

                if (entry.Value.SinceHeard >= ExpirySeconds)
                {
                    Release(entry.Key, entry.Value);
                    HostBrawls.Remove(entry.Key);
                }
            }
        }
    }
}
