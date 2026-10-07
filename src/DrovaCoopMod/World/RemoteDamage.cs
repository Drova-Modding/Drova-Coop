using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.Poise;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Puts the blows other players landed onto the local player. The receiving half of <see cref="DamageWatch"/>.
    /// Goes through <c>ChangeHealth</c>, never <c>_curHealth</c>, so the knockout blocker (<see cref="DownedState"/>)
    /// runs and <c>_isDead</c> is never latched.
    /// </summary>
    internal static class RemoteDamage
    {
        /// <summary>Applies everything that has arrived for the local player. Call once per frame.</summary>
        internal static void Update()
        {
            if (!CoopSession.IsLive) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            Health health = player.GetHealth();
            if (health == null) return;

            while (CoopSession.TryTakeDamage(out DamageReportMessage report))
            {
                Apply(player, health, report);
            }
        }

        /// <summary>Teammates' reported blows applied to the player here.</summary>
        internal static int Applied { get; private set; }

        private static void Apply(Actor player, Health health, in DamageReportMessage report)
        {
            try
            {
                ApplyBlow(player, health, report);
            }
            finally
            {
                DownedState.TeammateBlow = false;
            }
        }

        private static void ApplyBlow(Actor player, Health health, in DamageReportMessage report)
        {
            try
            {
                // Just stood up: the local hit receiver is off for a moment, so relayed blows are too.
                if (DownedState.IsGraced) return;

                // Asked again at apply time: a queued blow from the previous arena round must not land.
                if (report.Origin != DamageReportMessage.OriginCreature && !Arena.AllowsHarm(report.Attacker)) return;

                if (!RemoteEnemyHealth.TryResolveType(report.DamageType, out DamageStatDesc type)) return;

                if (report.Origin != DamageReportMessage.OriginCreature)
                {
                    MelonLogger.Msg($"[Damage] a teammate's blow {report.Sequence} of {report.Amount:0.#} arrives at health {health.CurHealth}.");
                }

                // No source: a source runs its own damage stats over the value, which already includes them.
                HealthChange change = new(null, new HealthChangeValue(report.Amount, type));

                // Only the permission. The game sizes the wound from what lands, against this player's armor.
                if (report.AppliesBleed != 0)
                {
                    change.SetAppliesBleed(true);
                }

                // Raw poise, resolved against this machine's stance numbers. See RemoteEnemyHealth.ApplyPoise.
                if (report.PoiseDamage > 0f)
                {
                    change.SetPotentialPoiseDmg(report.PoiseDamage);
                    change.AfterHealthChangedEvent.AddEventListener(new Action<HealthChangeArgs>(args => RemoteEnemyHealth.ApplyPoise(player, args, "[Dmg]")));
                }

                // Resolved stays false when ChangeHealth refuses outright (dialogue, mid-knockout).
                int landed = 0;
                int blocked = 0;
                bool resolved = false;
                change.AfterHealthChangedEvent.AddEventListener(new Action<HealthChangeArgs>(args =>
                {
                    if (args == null) return;
                    landed = args.ActualChange;
                    blocked = args.BlockedValue;
                    resolved = true;
                }));

                // Read before the blow, which can end the guard it breaks.
                bool guarding = BlockVerdicts.IsGuarding(player);

                // A Hardcore fall from friendly fire is not a bleed-out (DownedState).
                DownedState.TeammateBlow = report.Origin != DamageReportMessage.OriginCreature;

                // So DamageWatch, which sits on the same funnel, does not echo this write.
                using (RemoteApply<HealthScope>.Enter())
                {
                    health.ChangeHealth(change);
                    Applied++;

                    // PvP floor: the armor math truncates to zero with no minimum. Not for creatures, blocked
                    // or refused blows.
                    if (report.Origin == DamageReportMessage.OriginPlayer
                        && report.Amount > 0 && resolved && landed == 0 && blocked == 0)
                    {
                        HealthChange floorBlow = new(null, new HealthChangeValue(1, type));
                        floorBlow.SetIgnoreArmor();
                        health.ChangeHealth(floorBlow);
                    }
                }

                // Only a player's blow is answered. A creature's attacker is not a puppet.
                if (report.Origin == DamageReportMessage.OriginPlayer && resolved)
                {
                    // The game reports a blow as a negative health change.
                    BlockVerdicts.Answer(report.Attacker, report.Sequence, player, guarding, Math.Abs(landed));
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Dmg] applying a reported blow failed: " + e);
            }
        }

        /// <summary>Plays a teammate's block verdict on a blow this player struck out on their body.</summary>
        internal static void TakeVerdict(in BlockVerdictMessage message)
        {
            try
            {
                if (!RemotePlayers.Bodies.TryGetValue(message.Victim, out RemotePlayer? body)) return;
                if (body == null || body.Body == null) return;

                BlockVerdicts.Apply(message.Victim, message.Sequence, body.Body, message.Verdict);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Dmg] taking a teammate's block verdict failed: " + e);
            }
        }
    }
}
