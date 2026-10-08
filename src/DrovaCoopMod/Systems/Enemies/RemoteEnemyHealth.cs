using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.ABI;
using Il2CppDrova.Poise;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Creature health across machines. The host runs the real damage calculation on blows clients report,
    /// and clients write the absolute figure it sends back without recalculating, which would apply armor twice.
    /// </summary>
    internal sealed class RemoteEnemyHealth : ICoopSystem
    {
        // Deaths announced before their bodies streamed in.
        private static readonly List<string> PendingKnownDeaths = new();

        private static readonly Queue<EnemyHealthMessage> IncomingEnemyHealth = new();

        // Host only.
        private static readonly Queue<EnemyDamageMessage> IncomingEnemyDamage = new();
        private static readonly Dictionary<PlayerId, uint> LastEnemyDamageSequence = new();
        private static readonly Queue<EnemyDamageMessage> IncomingLeasedDamage = new();
        private static uint _nextEnemyDamageSequence;
        private static uint _lastLeasedDamageSequence;

        /// <summary>How many reported blows the host's world refused (invincible or blocked). For <c>coop_status</c>.</summary>
        internal static int Refused { get; private set; }

        /// <summary>How many brawl blows were stopped from killing. For <c>coop_status</c>.</summary>
        internal static int BrawlSaves { get; private set; }

        /// <summary>Forgets the counters. Called when a session ends.</summary>
        private static void Reset()
        {
            Refused = 0;
            BrawlSaves = 0;
        }

        /// <summary>Applies whatever has arrived for this machine's role.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                ApplyReportedBlows();
                return;
            }

            if (CoopSession.State == CoopSessionState.Joined)
            {
                ApplyAuthoritativeHealth();
            }
        }

        /// <summary>Forgets everything. Called when the world goes away.</summary>
        private static void Clear()
        {
            PendingKnownDeaths.Clear();
        }

        /// <summary>Records a creature to lay out once its body arrives.</summary>
        internal static void QueueKnownDeath(string guid)
        {
            if (!string.IsNullOrEmpty(guid))
            {
                PendingKnownDeaths.Add(guid);
            }
        }

        /// <summary>
        /// Deals the blows clients reported, through the game's own damage path. No source is named, since that
        /// would reapply the attacker's offensive stats and crit. Threat goes through <see cref="EnemyThreat"/>.
        /// </summary>
        private static void ApplyReportedBlows()
        {
            while (TryTakeEnemyDamage(out EnemyDamageMessage report))
            {
                Apply(report);
            }
        }

        private static void Apply(in EnemyDamageMessage report)
        {
            try
            {
                if (!EnemyRegistry.TryResolveOnHost(report.NetId, out Actor? actor)) return;

                Health? health = actor.GetHealth();
                if (health == null || health.IsDead) return;

                if (!TryResolveType(report.DamageType, out DamageStatDesc type)) return;

                HealthChange change = new(null, new HealthChangeValue(report.Amount, type));

                if (report.AppliesBleed != 0)
                {
                    change.SetAppliesBleed(true);
                }

                if (report.PoiseDamage > 0f)
                {
                    change.SetPotentialPoiseDmg(report.PoiseDamage);
                    change.AfterHealthChangedEvent.AddEventListener(
                        new Action<HealthChangeArgs>(args => ApplyPoise(actor, args, "[Enemy]")));
                }

                // Null when the sender's body is already gone.
                Actor? attacker = AttackerBody(report.Attacker);
                string? attackerGuid = attacker != null ? EnemyRegistry.GuidOf(attacker) : null;
                if (attackerGuid?.Length == 0) attackerGuid = null;

                // A brawl blow may not kill, and the brawl's own knockout only runs where the brawl started.
                // The game's ReviveBlocker catches a lethal wound. An authored blocker on the creature fires first.
                bool brawl = (report.Flags & EnemyDamageMessage.FlagBrawl) != 0;
                bool[] saved = [false];
                ReviveBlocker? guard = null;

                if (brawl)
                {
                    guard = new ReviveBlocker();
                    guard.ReviveEvent.AddEventListener(new Action<Entity>(_ => saved[0] = true));
                    health.AddDamageBlocker(guard.Cast<IDamageBlocker>());
                }

                int before = health.CurHealth;

                // The blow scope, not the health scope: a kill here is this machine's own news and must be broadcast.
                using (RemoteApply<EnemyBlowScope>.Enter())
                {
                    // The threat entry is filed synchronously inside ChangeHealth, which passes no attacker.
                    EnemyThreat.CreditTo(attackerGuid);

                    try
                    {
                        health.ChangeHealth(change);
                    }
                    finally
                    {
                        EnemyThreat.CreditTo(null);

                        if (guard != null)
                        {
                            health.RemoveDamageBlocker(guard.Cast<IDamageBlocker>());
                        }
                    }
                }

                if (saved[0])
                {
                    FinishBrawlKnockout(actor, health);
                }
                else if (report.Amount > 0 && health.CurHealth == before && !health.IsDead
                    && (health.IsInvincible || health.HasDamageBlockerActive))
                {
                    // Logged because a refused blow otherwise vanishes silently on the client.
                    Refused++;

                    if (Refused == 1 || Refused % 25 == 0)
                    {
                        MelonLogger.Msg($"[Enemy] a teammate's blow was refused by this world " +
                            $"({(health.IsInvincible ? "the creature is invincible" : "a damage blocker is active")}); " +
                            "usually a quest still protects it. Refused so far: " + Refused);
                    }
                }

                // A creature in this player's own brawl must not turn on a teammate outside it. In the arena the teammate joins.
                if (attacker != null && EngageWatch.IsBrawling(report.NetId))
                {
                    ArenaTeammates.TryJoin(actor, attacker);
                }
                else if (attacker != null && attackerGuid != null)
                {
                    EnemyThreat.Aggro(actor, attacker, attackerGuid);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] applying a reported blow to a creature failed: " + e);
            }
        }

        /// <summary>Does what <c>BrawlService</c> does on defeat: three health and the game's own knockout.</summary>
        private static void FinishBrawlKnockout(Actor actor, Health health)
        {
            try
            {
                using (RemoteApply<EnemyHealthScope>.Enter())
                {
                    health.SetHealth(3, null);
                }

                Il2CppDrova.Brawl.BrawlActor brawler = new(actor);
                brawler.Knockout();

                BrawlSaves++;

                MelonLogger.Msg("[Enemy] a teammate's brawl blow would have killed; the creature was " +
                    "knocked out at three health instead, the way the brawl itself would have done it.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] knocking out a brawl loser failed: " + e);
            }
        }

        /// <summary>The local body standing in for the player who reported a blow.</summary>
        internal static Actor? AttackerBody(PlayerId attacker)
        {
            return RemotePlayers.Bodies.TryGetValue(attacker, out RemotePlayer body) ? body.Body : null;
        }

        /// <summary>
        /// Applies the poise loss of a resolved reported blow, as <c>ABI_Bhvr_HitEffect_Damage.HandlePoise</c> would on
        /// the attacker's machine. The wire figure is raw, so <c>DamageCalculator.CalcPoiseDamage</c> accounts for blocking.
        /// </summary>
        internal static void ApplyPoise(Actor? actor, HealthChangeArgs? args, string tag)
        {
            try
            {
                if (args == null || actor == null) return;

                HealthChange? change = args.HealthChange;
                if (change == null) return;

                float raw = change.PotentialPoiseDmg;
                if (raw <= 0f) return;

                PoiseController? poise = actor.PoiseController;
                if (poise == null) return;

                // The last factor is the attacker's, which is 1 for a player.
                float loss = DamageCalculator.CalcPoiseDamage(
                    raw, args.ActualChange, args.BlockedValue, change.HitCount, 1f);

                if (loss <= 0f) return;

                poise.ChangePoise(-loss);
            }
            catch (Exception e)
            {
                // Runs inside the game's event dispatch, where a throw would abandon the health change.
                MelonLogger.Error(tag + " applying the stance a reported blow cost failed: " + e);
            }
        }

        /// <summary>Writes the health the host reports onto this client's bodies.</summary>
        private static void ApplyAuthoritativeHealth()
        {
            while (TryTakeEnemyHealth(out EnemyHealthMessage message))
            {
                Write(message);
            }

            ApplyKnownDeaths();
        }

        private static void Write(in EnemyHealthMessage message)
        {
            try
            {
                // A live local brawl owns the figure. A message in flight when the lease began would heal it.
                if (EngageWatch.IsBrawling(message.NetId)) return;

                // A zero is a death the death watch never hears (scoped write below). Recorded even without a body here.
                if (message.Health == 0 && EnemyRegistry.TryGetBoundGuid(message.NetId, out string? deadGuid))
                {
                    EnemyRegistry.RecordDead(deadGuid);
                }

                if (!EnemyRegistry.TryGetBody(message.NetId, out Actor? actor)) return;

                Health? health = actor.GetHealth();
                if (health == null || health.IsDead) return;

                // Starting gear sets max health later and refills the current value, overwriting an early write.
                // Maximums are not compared because difficulty is local and they may never agree.
                if (!actor.IsInitialized) return;

                // Clamped to the local maximum, which can differ from the host's.
                int wanted = message.Health;
                if (wanted > health.MaxHealth) wanted = health.MaxHealth;

                if (health.CurHealth == wanted) return;

                using (RemoteApply<EnemyHealthScope>.Enter())
                {
                    health.SetHealth(wanted, null);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] writing a creature's health failed: " + e);
            }
        }

        /// <summary>Lays out bodies that streamed in already dead, as a load so no animation or reward fires.</summary>
        private static void ApplyKnownDeaths()
        {
            if (PendingKnownDeaths.Count == 0) return;

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            for (int index = PendingKnownDeaths.Count - 1; index >= 0; index--)
            {
                Actor? actor = entities.GetActorFromGuid(PendingKnownDeaths[index]);

                // Not before its save has loaded: see RemoteEnemyDeath.Apply.
                if (actor == null || !EnemyRegistry.IsReady(actor)) continue;

                PendingKnownDeaths.RemoveAt(index);

                try
                {
                    Health? health = actor.GetHealth();
                    if (health == null || health.IsDead) continue;

                    using (RemoteApply<EnemyHealthScope>.Enter())
                    {
                        health.SetHealth(0, null, new Action<HealthChangeArgs>(args => args?.SetIsLoadingArg(true)));
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[Enemy] laying out a creature that was already dead failed: " + e);
                }
            }
        }

        /// <summary>
        /// Resolves a wire damage type. Only physical, magical and pure cross, since only those are reachable by property.
        /// </summary>
        internal static bool TryResolveType(byte damageType, [NotNullWhen(true)] out DamageStatDesc? type)
        {
            switch (damageType)
            {
                case DamageReportMessage.TypeMagical:
                    if (DamageAccess.TryGetMagicalDamageType(out type)) return true;
                    break;

                case DamageReportMessage.TypePure:
                    if (DamageAccess.TryGetPureDamageType(out type)) return true;
                    break;
            }

            // Physical is the fallback, so a blow is never dropped.
            return DamageAccess.TryGetPhysicalDamageType(out type);
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<EnemyDamageMessage>(MessageIds.EnemyDamage, OnEnemyDamage);
            CoopSession.Register<EnemyHealthMessage>(MessageIds.EnemyHealth, OnEnemyHealth);
        }

        void ICoopSystem.EndSession()
        {
            IncomingEnemyHealth.Clear();
            IncomingEnemyDamage.Clear();
            IncomingLeasedDamage.Clear();
            _lastLeasedDamageSequence = 0;
            LastEnemyDamageSequence.Clear();
            _nextEnemyDamageSequence = 0;
        }

        void ICoopSystem.PlayerLeft(PlayerId player)
        {
            LastEnemyDamageSequence.Remove(player);
        }

        /// <summary>Tells everyone what a creature's health is.</summary>
        internal static void BroadcastEnemyHealth(EnemyNetId id, ushort health, ushort maxHealth)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyHealthMessage
            {
                NetId = id,
                Health = health,
                MaxHealth = maxHealth,
            });
        }

        /// <summary>Takes the next authoritative health figure that has arrived.</summary>
        private static bool TryTakeEnemyHealth(out EnemyHealthMessage message)
            => IncomingEnemyHealth.TryDequeue(out message);

        private static void OnEnemyHealth(INetPeer peer, EnemyHealthMessage message)
        {
            if (!CoopSession.IsFromHost()) return;

            // Superseded by the next figure, so drop the oldest.
            CoopSession.EnqueueLatest(IncomingEnemyHealth, message, CoopSession.MaxPendingDamage);
        }

        /// <summary>Reports a blow the local player landed on a host-owned creature. Sequenced so a resend is harmless.</summary>
        internal static void ReportEnemyDamage(EnemyNetId id, int amount, float poiseDamage, bool appliesBleed,
            byte damageType, byte flags = 0)
        {
            if (CoopSession.State != CoopSessionState.Joined) return;
            if (amount <= 0) return;

            NetworkAccess.SendToAll(BuildEnemyDamage(id, amount, poiseDamage, appliesBleed, damageType, flags));
        }

        private static EnemyDamageMessage BuildEnemyDamage(EnemyNetId id, int amount, float poiseDamage,
            bool appliesBleed, byte damageType, byte flags)
        {
            return new EnemyDamageMessage
            {
                Attacker = CoopSession.LocalPlayerId,
                NetId = id,
                Sequence = ++_nextEnemyDamageSequence,
                Amount = amount >= ushort.MaxValue ? ushort.MaxValue : (ushort)amount,
                PoiseDamage = poiseDamage,
                AppliesBleed = appliesBleed ? (byte)1 : (byte)0,
                DamageType = damageType,
                Flags = flags,
            };
        }

        /// <summary>Takes the next blow a client reported. Host only.</summary>
        private static bool TryTakeEnemyDamage(out EnemyDamageMessage message)
            => IncomingEnemyDamage.TryDequeue(out message);

        /// <summary>
        /// Relays the host player's blow into a brawl a client runs. While leased, the client's opponents are the
        /// live ones and the host's are held.
        /// </summary>
        internal static void SendLeasedEnemyDamage(PlayerId to, EnemyNetId id, int amount, float poiseDamage,
            bool appliesBleed, byte damageType)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;
            if (amount <= 0) return;

            CoopSession.TrySendToPlayer(BuildEnemyDamage(id, amount, poiseDamage, appliesBleed, damageType, EnemyDamageMessage.FlagBrawl), to);
        }

        /// <summary>Takes the next teammate blow the host relayed into a local brawl. Clients only.</summary>
        internal static bool TryTakeLeasedDamage(out EnemyDamageMessage message)
            => IncomingLeasedDamage.TryDequeue(out message);

        private static void OnEnemyDamage(INetPeer peer, EnemyDamageMessage message)
        {
            // The host relaying a teammate's blow into a brawl this player runs locally. Deduped by sequence.
            if (CoopSession.State == CoopSessionState.Joined)
            {
                if (!CoopSession.IsFromHost()) return;

                if (_lastLeasedDamageSequence >= message.Sequence) return;
                _lastLeasedDamageSequence = message.Sequence;

                CoopSession.EnqueueLatest(IncomingLeasedDamage, message, CoopSession.MaxPendingDamage);
                return;
            }

            if (CoopSession.State != CoopSessionState.Hosting) return;

            if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;

            message.Attacker = sender;

            // Counting a resend twice would double a player's damage.
            if (LastEnemyDamageSequence.TryGetValue(sender, out uint last) && message.Sequence <= last) return;

            LastEnemyDamageSequence[sender] = message.Sequence;

            CoopSession.EnqueueLatest(IncomingEnemyDamage, message, CoopSession.MaxPendingDamage);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (loaded) return;

            Clear();
            Reset();
        }
    }
}
