using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Applies the blows teammates reported to the local player. The receiving half of <see cref="DamageWatch"/>.
    /// Goes through <c>ChangeHealth</c> so the <see cref="DownedState"/> blocker runs and <c>_isDead</c> is never latched.
    /// </summary>
    internal sealed class RemoteDamage : ICoopSystem
    {
        /// <summary>
        /// Seconds after a dodge in which a creature's relayed blow still counts as dodged.
        /// The host struck this player's body as it stood a round trip ago, so a blow dodged here arrives late.
        /// </summary>
        private const float DodgeLagSeconds = 0.35f;

        /// <summary>How far from the player a creature may stand and still have struck them. Covers ranged attacks and follow lag.</summary>
        private const float CreatureReach = 400f;

        private static readonly Queue<DamageReportMessage> IncomingDamage = new();

        /// <summary>The highest blow already counted from each attacker, so a repeated report is harmless.</summary>
        private static readonly Dictionary<PlayerId, uint> LastDamageSequence = new();
        private static uint _nextDamageSequence;
        private static float _lastDodge = float.NegativeInfinity;

        /// <summary>Teammates' reported blows applied to the player here.</summary>
        internal static int Applied { get; private set; }

        /// <summary>Blows of a teammate's brawl NPCs applied to the player here. For tests.</summary>
        internal static int BrawlBlows { get; private set; }

        /// <summary>Creatures' relayed blows refused because the player here dodged them.</summary>
        internal static int Dodged { get; private set; }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

            if (player.GetDodgeBehaviour() is { IsInDodge: true }) _lastDodge = Time.unscaledTime;

            if (IncomingDamage.Count == 0) return;

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
                // Just stood up. The local hit receiver is off for a moment, so relayed blows are too.
                if (DownedState.IsGraced) return;

                // Asked again at apply time, so a queued blow from the previous arena round does not land.
                bool allowed = report.Origin switch
                {
                    DamageReportMessage.OriginCreature => IsCreatureNear(report.Creature, player),
                    DamageReportMessage.OriginBrawl => IsBrawlerNear(report, player),
                    _ => Arena.AllowsHarm(report.Attacker),
                };
                if (!allowed) return;

                bool fromNpc = report.Origin != DamageReportMessage.OriginPlayer;

                // The game's dodge switches the hit receiver off, which this write would go around.
                if (fromNpc && Time.unscaledTime - _lastDodge <= DodgeLagSeconds)
                {
                    Dodged++;
                    return;
                }

                if (!RemoteEnemyHealth.TryResolveType(report.DamageType, out DamageStatDesc type)) return;

                if (!fromNpc)
                {
                    MelonLogger.Msg($"[Damage] a teammate's blow {report.Sequence} of {report.Amount:0.#} arrives at health {health.CurHealth}.");
                }

                // No source, since a source runs its own damage stats over a value that already includes them.
                int amount = fromNpc ? EnemyScaling.ScaleRelayed(report.Amount) : report.Amount;

                // No legit blow needs more than a full health bar before armor.
                amount = Math.Min(amount, Math.Max(1, health.MaxHealth));
                HealthChange change = new(null, new HealthChangeValue(amount, type));

                // Only the permission. The game sizes the wound from what lands, against this player's armor.
                if (report.AppliesBleed != 0)
                {
                    change.SetAppliesBleed(true);
                }

                // Raw poise, resolved against this machine's stance numbers.
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

                // A Hardcore fall from friendly fire or in a brawl is a knockout, not a bleed-out.
                DownedState.TeammateBlow = report.Origin != DamageReportMessage.OriginCreature;

                // So DamageWatch, which sits on the same funnel, does not echo this write.
                using (RemoteApply<HealthScope>.Enter())
                {
                    health.ChangeHealth(change);
                    Applied++;
                    if (report.Origin == DamageReportMessage.OriginBrawl) BrawlBlows++;

                    // PvP floor, since the armor math truncates to zero with no minimum.
                    // Not for creatures, blocked or refused blows.
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

        /// <summary>
        /// Whether a creature's relayed blow or effect may land here.
        /// Only the host's creatures report, and the one named must be alive near this player.
        /// </summary>
        internal static bool IsCreatureNear(EnemyNetId creature, Actor player)
        {
            if (!CoopSession.SharesWorld || !CoopSession.IsFromHost()) return false;

            return EnemyRegistry.TryGetBody(creature, out Actor? body) && IsAliveNear(body, player);
        }

        /// <summary>
        /// Whether an NPC of a teammate's brawl may have struck the player here.
        /// On the host the brawl must be leased to the reporter. A client trusts the host, which checked it on the way through.
        /// </summary>
        private static bool IsBrawlerNear(in DamageReportMessage report, Actor player)
        {
            if (!CoopSession.SharesWorld) return false;

            if (CoopSession.IsFromHost()) return EnemyRegistry.TryGetBody(report.Creature, out Actor? body) && IsAliveNear(body, player);

            return CoopSession.State == CoopSessionState.Hosting
                && EnemyEngagements.TryGetLease(report.Creature, out PlayerId lessee) && lessee == report.Attacker
                && EnemyRegistry.TryResolveOnHost(report.Creature, out Actor? held) && IsAliveNear(held, player);
        }

        private static bool IsAliveNear(Actor body, Actor player)
        {
            Health? health = body.GetHealth();
            if (health == null || health.IsDead) return false;

            return (body.GetFeetPosition() - player.GetFeetPosition()).sqrMagnitude <= CreatureReach * CreatureReach;
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
        /// Tells another player the local player hit them.
        /// The amount is before the victim's defense, which their own game applies.
        /// </summary>
        internal static void ReportDamage(PlayerId victim, int amount, float poiseDamage, bool appliesBleed,
            byte damageType, byte origin = DamageReportMessage.OriginPlayer, EnemyNetId? creature = null)
        {
            if (!CoopSession.IsLive) return;

            // Friendly fire is checked here too, for the frame the host turned it off. Creature blows only reach
            // the victim this way, so they are never refused.
            if (origin == DamageReportMessage.OriginPlayer && !Arena.AllowsHarm(victim)) return;

            if (!victim.IsAssigned || victim == CoopSession.LocalPlayerId) return;
            if (amount <= 0) return;

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
                Creature = creature ?? EnemyNetId.None,
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

        /// <summary>Blows are queued because there may be no body yet.</summary>
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

                // Only the host's creatures report blows on players, so a client's creature blow is forged.
                // A brawl blow only from the player the brawl is leased to.
                if (message.Origin == DamageReportMessage.OriginCreature) return;
                if (message.Origin == DamageReportMessage.OriginBrawl
                    ? !EnemyEngagements.TryGetLease(message.Creature, out PlayerId lessee) || lessee != sender
                    : !Arena.AllowsHarm(sender, message.Victim))
                {
                    return;
                }

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

            if (!CoopSession.IsFromHost()) return;
            if (message.Victim != CoopSession.LocalPlayerId) return;
            if (message.Origin == DamageReportMessage.OriginPlayer && !Arena.AllowsHarm(message.Attacker)) return;

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
