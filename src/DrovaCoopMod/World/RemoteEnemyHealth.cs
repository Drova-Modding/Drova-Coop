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
                        new Action<HealthChangeArgs>(args => ApplyPoise(actor, args)));
                }

                // The body the blow came from, so the creature can be told who hit it. Absent only for a
                // report that arrived after its sender's body went, which is a blow with nobody left to
                // fight over.
                Actor? attacker = AttackerBody(report.Attacker);
                string? attackerGuid = attacker != null ? attacker.GetGuidComponent()?.GuidString : null;

                using (RemoteApply<EnemyHealthScope>.Enter())
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
        /// Takes the stance the blow cost, once the health change carrying it has resolved. The same step
        /// <see cref="RemoteDamage"/> performs for a player, and missing here for the same reason: the
        /// behavior that would normally do it lives on the attacker's machine.
        /// </summary>
        private static void ApplyPoise(Actor actor, HealthChangeArgs args)
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

                // The factor the game reads off the attacker, and every blow arriving here was struck by
                // a real player on their own machine.
                float loss = DamageCalculator.CalcPoiseDamage(
                    raw, args.ActualChange, args.BlockedValue, change.HitCount, 1f);

                if (loss <= 0f) return;

                poise.ChangePoise(-loss);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] applying the stance a reported blow cost failed: " + e);
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
        /// Finds the damage type a reported blow should be answered by. The same three that cross for
        /// players, for the same reason: they are the only ones the game names by property, and there is
        /// no database to resolve an authored type from an id.
        /// </summary>
        private static bool TryResolveType(byte damageType, out DamageStatDesc type)
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

            return DamageAccess.TryGetPhysicalDamageType(out type);
        }
    }
}
