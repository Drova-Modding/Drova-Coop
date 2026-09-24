using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.ABI;
using Il2CppDrova.Poise;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Both ends of a creature's health: the host answering the blows clients report, and a client
    /// writing the figure the host sends back.
    ///
    /// The two halves are asymmetric on purpose. A client sends the blow and the host answers it with
    /// a real damage calculation, because armor, resistances and the blockers that keep a creature alive
    /// belong to the machine that owns it. What comes back is not the blow but the answer - an absolute
    /// health figure - which the client writes without calculating anything, because calculating it again
    /// would apply the creature's armor twice.
    /// </summary>
    internal static class RemoteEnemyHealth
    {
        // Deaths the client has been told about but whose bodies were not there to receive them. Kept
        // here rather than in the registry because it is a queue of work rather than a fact about the
        // world.
        private static readonly List<string> _pendingKnownDeaths = new();

        /// <summary>
        /// How many reported blows the host's world refused outright - an invincible creature, an
        /// active damage blocker. For <c>coop_status</c>: without this a quest-protected NPC just
        /// silently cannot be hurt from a client and nothing anywhere says why.
        /// </summary>
        internal static int Refused { get; private set; }

        /// <summary>How many brawl blows were stopped from killing. For <c>coop_status</c>.</summary>
        internal static int BrawlSaves { get; private set; }

        /// <summary>Forgets the counters. Called when a session ends.</summary>
        internal static void Reset()
        {
            Refused = 0;
            BrawlSaves = 0;
        }

        /// <summary>
        /// Applies whatever has arrived for this machine's role. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update()
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
        internal static void Clear()
        {
            _pendingKnownDeaths.Clear();
        }

        /// <summary>
        /// Deals the blows clients reported, through the game's own damage path.
        ///
        /// No source, and that is the same load-bearing detail as in player damage. Naming the
        /// attacker would run their offensive stats over a figure that already includes them, and it
        /// would re-roll a crit their machine has already settled. What a source would have carried -
        /// aggro, threat, kill credit - either happened on the attacker's own machine already or is
        /// arranged separately, which for the two that decide who a creature fights means
        /// <see cref="EnemyThreat"/>.
        /// </summary>
        private static void ApplyReportedBlows()
        {
            while (CoopSession.TryTakeEnemyDamage(out EnemyDamageMessage report))
            {
                Apply(report);
            }
        }

        private static void Apply(in EnemyDamageMessage report)
        {
            try
            {
                if (!EnemyRegistry.TryResolveOnHost(report.NetId, out Actor actor)) return;

                Health health = actor.GetHealth();
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

                // The body the blow came from, so the creature can be told who hit it. Absent only for a
                // report that arrived after its sender's body went, which is a blow with nobody left to
                // fight over.
                Actor? attacker = AttackerBody(report.Attacker);
                string? attackerGuid = attacker != null ? attacker.GetGuidComponent()?.GuidString : null;

                // A brawl blow is not allowed to kill, and the machinery that would have stopped it -
                // the brawl's own clamp-at-three-and-knock-out - runs only on the machine the brawl was
                // started on. The guard is the game's own ReviveBlocker: it steps in when the wound
                // would leave one health or less, sets two, and reports the damage as zero. If the
                // creature carries an authored one (most quest NPCs that may not die do) that one fires
                // first and does its own knockout; this one only catches the rest.
                bool brawl = (report.Flags & EnemyDamageMessage.FlagBrawl) != 0;
                bool[] saved = { false };
                ReviveBlocker? guard = null;

                if (brawl)
                {
                    guard = new ReviveBlocker();
                    guard.ReviveEvent.AddEventListener(new Action<Entity>(_ => saved[0] = true));
                    health.AddDamageBlocker(guard.Cast<IDamageBlocker>());
                }

                int before = health.CurHealth;

                // The blow scope rather than the health scope, and the difference is the whole of a bug
                // this cost a playthrough. A blow arriving from a teammate is a fight resolving HERE for
                // the first time; if it kills, this machine is the only one that ran the numbers and the
                // death is its own news. Under the health scope the death watch treated it as an echo of
                // itself and said nothing, so the creature died on this machine and stayed "alive" in
                // everybody's book - which is precisely what asks for a new body later.
                using (RemoteApply<EnemyBlowScope>.Enter())
                {
                    // Around the change rather than before it, because the threat entry is filed from
                    // inside it - the game raises the damage event synchronously and hands no attacker
                    // along. Cleared in a finally so a throw cannot leave the next blow crediting the
                    // wrong player.
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
                    // Surfaced because the silence was indistinguishable from a bug: the client's own
                    // copy is write-protected by this mod, so a blow this world refuses simply vanishes,
                    // and "the client cannot hurt these bandits" had no line anywhere saying the quest
                    // still protects them.
                    Refused++;

                    if (Refused == 1 || Refused % 25 == 0)
                    {
                        MelonLogger.Msg($"[Enemy] a teammate's blow was refused by this world " +
                            $"({(health.IsInvincible ? "the creature is invincible" : "a damage blocker is active")}); " +
                            "usually a quest still protects it. Refused so far: " + Refused);
                    }
                }

                if (attacker != null && attackerGuid != null)
                {
                    EnemyThreat.Aggro(actor, attacker, attackerGuid);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] applying a reported blow to a creature failed: " + e);
            }
        }

        /// <summary>
        /// Lays a creature out the way the brawl that beat it would have.
        ///
        /// What <c>BrawlService</c> does at the moment of defeat, on the machine that has no brawl:
        /// three health instead of a corpse, and the game's own knockout so every AI that looks at the
        /// body sees a settled fight. The knockout flag then rides the snapshot stream back to the
        /// machine the brawl runs on, whose brawl sees its opponent down and ends itself.
        /// </summary>
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

        /// <summary>
        /// The body standing in for the player who reported a blow.
        ///
        /// It is that body rather than the player it represents that has to be named, because everything
        /// the creature will do with the answer - walking to it, swinging at it, holding it in a combat
        /// set - is done against something that exists in this world.
        /// </summary>
        private static Actor? AttackerBody(PlayerId attacker)
        {
            return RemotePlayers.Bodies.TryGetValue(attacker, out RemotePlayer? body) ? body.Body : null;
        }

        /// <summary>
        /// Takes the stance a reported blow cost, once the health change carrying it has resolved. For a
        /// creature here and for the local player in <see cref="RemoteDamage"/>.
        ///
        /// The figure on the wire is what the blow was worth, not what it takes. Draining it directly
        /// staggered a player who had just perfectly blocked. <c>DamageCalculator.CalcPoiseDamage</c> is
        /// the game's own answer, and every input it needs beyond the raw figure belongs to this machine
        /// and is only known once <c>ChangeHealth</c> has run.
        ///
        /// This is the step <c>ABI_Bhvr_HitEffect_Damage.HandlePoise</c> performs after an ordinary hit.
        /// It is missing here because that behavior lives on the attacker's machine.
        /// </summary>
        /// <param name="actor">Who took the blow.</param>
        /// <param name="args">The resolved health change.</param>
        /// <param name="tag">The log tag of the caller.</param>
        internal static void ApplyPoise(Actor actor, HealthChangeArgs args, string tag)
        {
            try
            {
                if (args == null || actor == null) return;

                HealthChange change = args.HealthChange;
                if (change == null) return;

                float raw = change.PotentialPoiseDmg;
                if (raw <= 0f) return;

                PoiseController poise = actor.PoiseController;
                if (poise == null) return;

                // The factor the game reads off the attacker despite the parameter being named for the
                // target. It is one for a player, and every blow arriving here was struck by a real player.
                float loss = DamageCalculator.CalcPoiseDamage(
                    raw, args.ActualChange, args.BlockedValue, change.HitCount, 1f);

                if (loss <= 0f) return;

                poise.ChangePoise(-loss);
            }
            catch (Exception e)
            {
                // Swallowed here as well as around the caller, because this runs inside the game's own
                // event dispatch. An exception let out would abandon the rest of the health change.
                MelonLogger.Error(tag + " applying the stance a reported blow cost failed: " + e);
            }
        }

        /// <summary>
        /// Writes the health the host reports onto the bodies this client is showing.
        /// </summary>
        private static void ApplyAuthoritativeHealth()
        {
            while (CoopSession.TryTakeEnemyHealth(out EnemyHealthMessage message))
            {
                Write(message);
            }

            ApplyKnownDeaths();
        }

        private static void Write(in EnemyHealthMessage message)
        {
            try
            {
                // This player's live brawl owns the figure while it lasts. The host stops announcing a
                // leased creature's health, but a message already in flight when the lease began would
                // heal the fight back up.
                if (EngageWatch.IsBrawling(message.NetId)) return;

                // A zero is a death arriving as a number, and nothing else writes it down. The write
                // below happens inside a scope the death watch is deliberately deaf to, so without this
                // the graveyard stays empty and the registry keeps answering "the host has this one
                // standing" about a creature the host has just laid out - which is the silence that asks
                // for a new body when the chunk next streams in.
                //
                // Before the body is looked up, on purpose: this is knowledge about the creature rather
                // than about the object it will eventually land on, and the cases where there is no body
                // here yet are exactly the cases it exists for.
                if (message.Health == 0 && EnemyRegistry.TryGetBoundGuid(message.NetId, out string deadGuid))
                {
                    EnemyRegistry.RecordDead(deadGuid);
                }

                if (!EnemyRegistry.TryGetBody(message.NetId, out Actor actor)) return;

                Health health = actor.GetHealth();
                if (health == null || health.IsDead) return;

                // Held until this body has finished setting itself up. Equipping an actor's starting
                // gear writes its maximum health, and writing a maximum refills the current value - some
                // frames after the body exists. A figure written before that is silently replaced by full
                // health, so the creature would appear to heal itself the moment it spawned.
                //
                // Asked of the actor rather than by comparing the two maximums. Comparing them looks
                // stricter and is worse: the two machines can settle on genuinely different maximums -
                // difficulty is a local setting - and a gate that waits for them to agree then waits
                // forever, which reads in play as a creature that simply cannot be hurt.
                if (!actor.IsInitialized) return;

                // Clamped to what this body can hold, for the same reason. A figure above the local
                // maximum is not a reason to refuse the update; it is a reason to take as much of it as
                // fits, so the bar still falls when the creature is hit.
                int wanted = message.Health;
                if (wanted > health.MaxHealth) wanted = health.MaxHealth;

                if (health.CurHealth == wanted) return;

                using (RemoteApply<EnemyHealthScope>.Enter())
                {
                    // Straight to the figure rather than through a calculation. The host has already run
                    // one; running another here would answer the host's answer with this machine's armor.
                    health.SetHealth(wanted, null);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] writing a creature's health failed: " + e);
            }
        }

        /// <summary>
        /// Kills bodies that streamed in already dead.
        ///
        /// The graveyard's other half: the host said a creature was dead before this client had a body for
        /// it, and the body has now appeared. Applied as a load rather than a kill, so no animation plays
        /// and nothing is awarded for a death that happened before this player arrived.
        /// </summary>
        private static void ApplyKnownDeaths()
        {
            while (CoopSession.TryTakeKnownDeath(out string guid))
            {
                _pendingKnownDeaths.Add(guid);
            }

            if (_pendingKnownDeaths.Count == 0) return;

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            for (int index = _pendingKnownDeaths.Count - 1; index >= 0; index--)
            {
                Actor actor = entities.GetActorFromGuid(_pendingKnownDeaths[index]);
                if (actor == null) continue;

                _pendingKnownDeaths.RemoveAt(index);

                try
                {
                    Health health = actor.GetHealth();
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
        /// Finds the damage type a reported blow should be answered by, for a creature here and for the
        /// local player in <see cref="RemoteDamage"/>.
        ///
        /// Three types cross, not every type. They are the only ones the game names by property, and there
        /// is no database to resolve an authored type from an id. That covers a spell answered by wards
        /// rather than by armor, and leaves exotic authored types arriving as physical.
        /// </summary>
        internal static bool TryResolveType(byte damageType, out DamageStatDesc type)
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

            // Physical is both the default and the fallback: a build whose settings have no magical type
            // yet should still take the blow rather than ignore it.
            return DamageAccess.TryGetPhysicalDamageType(out type);
        }
    }
}
