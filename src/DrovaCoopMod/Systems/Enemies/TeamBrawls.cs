using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// A client's side of joinable scripted brawls: the local NPC copy accepts this player's swings in a teammate's brawl,
    /// and teammates' relayed blows land in this player's own leased brawl.
    /// </summary>
    internal sealed class TeamBrawls : ICoopSystem
    {
        /// <summary>Three missed keepalives, the same clock the host runs for client engagements.</summary>
        private const float ExpirySeconds = 15f;

        private sealed class Mark
        {
            public float SinceHeard;
            public bool Hostile;
            public bool Ally;
        }

        private static readonly Dictionary<EnemyNetId, Mark> TeammateBrawls = new();

        /// <summary>How many host brawls this player was invited into. For <c>coop_status</c>.</summary>
        internal static int Joined { get; private set; }

        /// <summary>Whether a teammate is in a scripted brawl right now, lingering epilogue included.</summary>
        internal static bool AnyTeammateBrawl => TeammateBrawls.Count > 0;

        /// <summary>Whether this NPC fights in a brawl the host or another client runs.</summary>
        internal static bool IsTeammatesBrawl(EnemyNetId id) => TeammateBrawls.ContainsKey(id);

        /// <summary>How many relayed teammate blows landed in this player's own brawl. For <c>coop_status</c>.</summary>
        internal static int Assists { get; private set; }

        /// <summary>Forgets everything and takes the hostility marks back.</summary>
        internal static void Reset()
        {
            foreach (KeyValuePair<EnemyNetId, Mark> entry in TeammateBrawls)
            {
                Release(entry.Key, entry.Value);
            }

            TeammateBrawls.Clear();
            Joined = 0;
            Assists = 0;
        }

        /// <summary>Applies what the host announced and relayed.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.JoinedWorld)
            {
                if (TeammateBrawls.Count > 0) Reset();
                return;
            }

            try
            {
                while (EnemyEngagements.TryTakeHostEngage(out EnemyEngageMessage engage))
                {
                    Apply(engage);
                }

                while (RemoteEnemyHealth.TryTakeLeasedDamage(out EnemyDamageMessage blow))
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

            BusyNpcs.Note(message);

            bool known = TeammateBrawls.TryGetValue(message.NetId, out Mark? mark);

            if (message.Mode != EnemyEngageMessage.ModeBrawl)
            {
                if (!known) return;

                Release(message.NetId, mark!);
                TeammateBrawls.Remove(message.NetId);
                return;
            }

            if (!known)
            {
                mark = new Mark();
                TeammateBrawls[message.NetId] = mark;
            }

            mark!.SinceHeard = 0f;
            mark.Ally = message.Ally;
            Invite(message.NetId, mark);
        }

        /// <summary>Makes the local copy accept this player's swings. Retried until the body has streamed in.</summary>
        private static void Invite(EnemyNetId id, Mark mark)
        {
            // An NPC on the teammate's side is nobody's enemy here.
            if (mark.Hostile || mark.Ally) return;

            if (!EnemyRegistry.BoundBodies.TryGetValue(id, out Actor? body) || body == null) return;
            if (!PlayerAccess.TryGetPlayer(out Actor? player)) return;

            string guid = EnemyRegistry.GuidOf(player);
            if (guid.Length == 0) return;

            EnemyThreat.Aggro(body, player, guid);
            mark.Hostile = true;
            Joined++;

            MelonLogger.Msg("[Engage] a teammate is brawling this NPC; this player's blows now count " +
                "toward that fight.");
        }

        private static void Release(EnemyNetId id, Mark mark)
        {
            if (!mark.Hostile) return;

            try
            {
                if (!EnemyRegistry.BoundBodies.TryGetValue(id, out Actor? body) || body == null) return;

                PlayerAccess.TryGetPlayer(out Actor? player);
                string guid = player != null ? EnemyRegistry.GuidOf(player) : string.Empty;

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
            if ((report.Flags & EnemyDamageMessage.FlagThreat) != 0)
            {
                FighterDrive.TakeThreat(report);
                return;
            }

            if (!EngageWatch.IsBrawling(report.NetId)) return;
            if (!EnemyRegistry.BoundBodies.TryGetValue(report.NetId, out Actor? body) || body == null) return;

            Health health = body.GetHealth();
            if (health == null || health.IsDead) return;

            if (body.CustomStats == null || !body.CustomStats.IsInBrawl) return;

            // A teammate knocked out of this brawl stays out, so a side whose players all went down loses.
            Actor? attacker = RemoteEnemyHealth.AttackerBody(report.Attacker);
            if (ArenaTeammates.IsOut(attacker)) return;

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

            // Under the health scope, a death this causes would be taken for an echo and never announced.
            using (RemoteApply<EnemyBlowScope>.Enter())
            {
                health.ChangeHealth(change);
            }

            Assists++;
            ArenaTeammates.TryJoin(body, attacker);

            if (Assists == 1)
            {
                MelonLogger.Msg("[Engage] a teammate's blow was carried into this player's brawl.");
            }
        }

        private static void Expire(float unscaledDeltaTime)
        {
            if (TeammateBrawls.Count == 0) return;

            foreach (KeyValuePair<EnemyNetId, Mark> entry in TeammateBrawls)
            {
                entry.Value.SinceHeard += unscaledDeltaTime;

                // Retried while the mark lives, so a late-streamed body still joins.
                Invite(entry.Key, entry.Value);

                if (entry.Value.SinceHeard >= ExpirySeconds)
                {
                    Release(entry.Key, entry.Value);
                    TeammateBrawls.Remove(entry.Key);
                }
            }
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
