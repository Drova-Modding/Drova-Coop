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
    /// A client's side of joinable scripted brawls.
    ///
    /// Two directions. When the host announces its own brawl, the local copy of that NPC is given the
    /// standing a first blow would earn, so this player's swings connect on a body that is otherwise a
    /// neutral the hit filters turn away; the blows themselves already travel as ordinary reported
    /// damage, and the host's live brawl answers them non-lethally with its own machinery. When this
    /// player's brawl holds the lease, the host relays its player's blows here, and they are applied
    /// into the live local fight - which is the only place they can mean anything, because the host's
    /// copy stands held and invincible for the duration.
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

        private static readonly Dictionary<EnemyNetId, Mark> _hostBrawls = new();

        /// <summary>How many host brawls this player was invited into. For <c>coop_status</c>.</summary>
        internal static int Joined { get; private set; }

        /// <summary>Whether the host is in a scripted brawl right now, lingering epilogue included.</summary>
        internal static bool AnyHostBrawl => _hostBrawls.Count > 0;

        /// <summary>How many relayed teammate blows landed in this player's own brawl. For <c>coop_status</c>.</summary>
        internal static int Assists { get; private set; }

        /// <summary>Forgets everything and takes the hostility marks back. Called when a session ends.</summary>
        internal static void Reset()
        {
            foreach (KeyValuePair<EnemyNetId, Mark> entry in _hostBrawls)
            {
                Release(entry.Key, entry.Value);
            }

            _hostBrawls.Clear();
            Joined = 0;
            Assists = 0;
        }

        /// <summary>
        /// Applies what the host announced and relayed. Call once per frame from a loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State != CoopSessionState.Joined)
            {
                if (_hostBrawls.Count > 0) Reset();
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

            bool known = _hostBrawls.TryGetValue(message.NetId, out Mark? mark);

            if (message.Mode != EnemyEngageMessage.ModeBrawl)
            {
                if (!known) return;

                Release(message.NetId, mark!);
                _hostBrawls.Remove(message.NetId);
                return;
            }

            if (!known)
            {
                mark = new Mark();
                _hostBrawls[message.NetId] = mark;
            }

            mark!.SinceHeard = 0f;
            Invite(message.NetId, mark);
        }

        /// <summary>
        /// Makes the local copy accept this player's swings. Retried while the mark lives, because the
        /// body may not have streamed in when the announcement arrived.
        /// </summary>
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

                // Every body, not only this player's. The same brawl also relayed the host player's
                // blows onto this copy, and each of those was given its standing against the host's
                // puppet - hostilities with the same owner and the same ending as this one.
                EnemyThreat.Pardon(body, guid);
                mark.Hostile = false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] standing down from the host's brawl failed: " + e);
            }
        }

        /// <summary>
        /// Lands a teammate's relayed blow in this player's own live brawl.
        ///
        /// No guard is installed around it on purpose: the fight is real here, so the brawl's own
        /// clamp-at-three-and-knock-out answers a killing blow - putting a blocker in front of that is
        /// exactly the mistake that kept a capture from ever resolving. What protects the NPC instead
        /// is the gate: a blow for a body this player is not currently brawling is dropped.
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

            // The blow scope, as in RemoteEnemyHealth: the fight resolves here, so a death this blow causes is this
            // machine's news. Under the health scope it was taken for an echo - the host's blow killed Fulk in the
            // client's massacre brawl, nobody said so, and the host's copy stood up again in the client's world.
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
            if (_hostBrawls.Count == 0) return;

            foreach (KeyValuePair<EnemyNetId, Mark> entry in _hostBrawls)
            {
                entry.Value.SinceHeard += unscaledDeltaTime;

                // Not yet hostile because the body had not streamed in - keep trying while the mark is
                // alive, so a late chunk still joins the fight.
                Invite(entry.Key, entry.Value);

                if (entry.Value.SinceHeard >= ExpirySeconds)
                {
                    Release(entry.Key, entry.Value);
                    _hostBrawls.Remove(entry.Key);
                }
            }
        }
    }
}
