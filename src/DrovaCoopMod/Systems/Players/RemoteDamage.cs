using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Puts the blows other players landed onto the local player. The receiving half of <see cref="DamageWatch"/>.
    /// Goes through <c>ChangeHealth</c>, never <c>_curHealth</c>, so the knockout blocker (<see cref="DownedState"/>)
    /// runs and <c>_isDead</c> is never latched.
    /// </summary>
    internal sealed class RemoteDamage : ICoopSystem
    {
        // The highest blow already counted from each attacker makes a repeated report harmless.
        private static readonly Queue<DamageReportMessage> IncomingDamage = new();
        private static readonly Dictionary<PlayerId, uint> LastDamageSequence = new();
        private static uint _nextDamageSequence;

        /// <summary>Teammates' reported blows applied to the player here.</summary>
        internal static int Applied { get; private set; }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            Health? health = player.GetHealth();
            if (health == null) return;

            while (TryTakeDamage(out DamageReportMessage report))
            {
                Apply(player, health, report);
            }
        }

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

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<DamageReportMessage>(MessageIds.DamageReport, OnDamageReport);
        }

        void ICoopSystem.EndSession()
        {
            IncomingDamage.Clear();
            LastDamageSequence.Clear();
            _nextDamageSequence = 0;
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            // Ids are reused and the next holder counts from one, so a stale high-water mark would swallow their hits.
            LastDamageSequence.Remove(player);
        }

        /// <summary>
        /// Tells another player the local player hit them. The figure is before their defense, since the victim's
        /// game resists it. See <see cref="DamageReportMessage"/>.
        /// </summary>
        internal static void ReportDamage(PlayerId victim, int amount, float poiseDamage, bool appliesBleed,
            byte damageType, byte origin = DamageReportMessage.OriginPlayer)
        {
            if (!CoopSession.IsLive) return;

            // Friendly fire is checked here too, for the frame the host turned it off. Creature blows only reach
            // the victim this way, so they are never refused.
            if (origin != DamageReportMessage.OriginCreature && !Arena.AllowsHarm(victim)) return;

            if (!victim.IsAssigned || victim == CoopSession.LocalPlayerId) return;
            if (amount <= 0) return;

            // Capped rather than wrapped.
            if (amount > ushort.MaxValue)
            {
                amount = ushort.MaxValue;
            }

            DamageReportMessage report = new()
            {
                Attacker = CoopSession.LocalPlayerId,
                Victim = victim,
                Amount = (ushort)amount,
                Sequence = unchecked(++_nextDamageSequence),

                // A NaN would reach the far side as arithmetic on their stance.
                PoiseDamage = poiseDamage > 0f && !float.IsNaN(poiseDamage) && !float.IsInfinity(poiseDamage) ? poiseDamage : 0f,
                AppliesBleed = appliesBleed ? (byte)1 : (byte)0,
                DamageType = damageType,
                Origin = origin,
            };

            // Remembered first so the answering verdict is recognized. Only a player's blow is answered.
            if (origin == DamageReportMessage.OriginPlayer) BlockVerdicts.Await(victim, report.Sequence);

            if (NetworkAccess.Role == NetRole.Host)
            {
                CoopSession.TrySendToPlayer(report, victim);
                return;
            }

            // A client's only peer is the host, and peer ids are machine-local, so the host addresses the victim.
            NetworkAccess.SendToAll(report);
        }

        /// <summary>Takes the next blow for the local player, oldest first. Queued because there may be no body yet.</summary>
        private static bool TryTakeDamage(out DamageReportMessage report)
            => IncomingDamage.TryDequeue(out report);

        private static void OnDamageReport(INetPeer peer, DamageReportMessage message)
        {
            if (!CoopSession.IsLive) return;

            if (message.Amount == 0) return;

            if (NetworkAccess.Role == NetRole.Host)
            {
                // The sender does not get to say who swung.
                if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

                message.Attacker = sender;

                if (message.Origin != DamageReportMessage.OriginCreature && !Arena.AllowsHarm(sender, message.Victim)) return;

                // A player's own swings land on their own machine, so a self-report is a bug or an exploit.
                if (message.Victim == sender) return;

                if (message.Victim == CoopSession.LocalPlayerId)
                {
                    Enqueue(message);
                    return;
                }

                if (!CoopSession.Players.ContainsKey(message.Victim)) return;

                CoopSession.TrySendToPlayer(message, message.Victim);
                return;
            }

            // Only the host sends these, and only blows addressed to this player.
            if (!CoopSession.IsFromHost()) return;
            if (message.Victim != CoopSession.LocalPlayerId) return;
            if (message.Origin != DamageReportMessage.OriginCreature && !Arena.AllowsHarm(message.Attacker)) return;

            Enqueue(message);
        }

        private static void Enqueue(in DamageReportMessage report)
        {
            // Reliable delivery makes a duplicate unlikely, not impossible, and the transport does not reject replays.
            if (LastDamageSequence.TryGetValue(report.Attacker, out uint last) && report.Sequence <= last) return;

            LastDamageSequence[report.Attacker] = report.Sequence;

            CoopSession.EnqueueLatest(IncomingDamage, report, CoopSession.MaxPendingDamage);
        }
    }
}
