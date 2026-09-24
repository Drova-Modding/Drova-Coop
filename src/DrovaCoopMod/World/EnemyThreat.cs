using Drova_Modding_API;
using Il2CppDrova;
using Il2CppDrova.AI.Modules;
using Il2CppDrova.Alignment;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Gives a client's reported blow the standing with a creature that striking it in person would have
    /// carried.
    ///
    /// A blow the host applies for somebody else has no attacker, and four separate things read the
    /// attacker. <see cref="RemoteEnemyHealth"/> builds its health change with a null source on purpose -
    /// naming one would run the attacker's stat multipliers over a figure that already includes them and
    /// re-roll a crit the other machine has already settled - but that same field is what
    /// <c>Health.CheckEvents</c> tests before it tags the attacker as a combat target, what
    /// <c>HealthBhvr_OnDmgGetAggro</c> reads to turn a neutral creature hostile, and what
    /// <c>AIHealthAdapter</c> turns into the threat entry that <c>ThreatTargetActorService</c> chooses
    /// targets by. Sourceless damage therefore hurts and means nothing: the creature loses health and
    /// carries on fighting whoever it was fighting.
    ///
    /// Which is what a fight looked like. Every point of threat a creature held belonged to the host,
    /// because only the host's swings ever arrived with a name on them - so it ran past the person actually
    /// hitting it to reach somebody on the other side of the map, and the client's blows registered as
    /// nothing but a shrinking health bar.
    ///
    /// So the consequences are supplied and the calculation is not. Everything here is what the game
    /// itself would have done had the source been set, done afterwards from the guid the report names, and
    /// nothing here touches the number.
    /// </summary>
    internal static class EnemyThreat
    {
        // Reused rather than built per blow: this is a set of exactly one, handed to a method that copies
        // out of it.
        private static readonly Il2CppSystem.Collections.Generic.HashSet<string> _oneAttacker = new();

        // What was added and can therefore be taken back. A hostility the game installs itself has an
        // owner to remove it - a brawl removes its alignment when the brawl ends - but the ones installed
        // here stand in for an attacker the creature cannot see, and only this class knows they exist.
        private static readonly List<Hostility> _installed = new();

        private sealed record Hostility(IntPtr Enemy, string AttackerGuid, IAlignment Alignment);

        private static string? _creditTo;
        private static bool _reported;

        /// <summary>How many hostilities were taken back after a duel ended. For <c>coop_status</c>.</summary>
        internal static int Pardoned { get; private set; }

        /// <summary>
        /// How many reported blows were given standing with the creature they hit. For <c>coop_status</c>.
        /// </summary>
        internal static int Credited { get; private set; }

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // Safe to patch despite being private: it is subscribed to Health.DamagedEvent as a delegate,
            // so a call to it always goes through the delegate and can never have been inlined.
            Hooking.TryPostfix(harmony, typeof(AIHealthAdapter), "OnDamageEventListener",
                typeof(EnemyThreat), nameof(OnDamageEventListenerPostfix));
        }

        /// <summary>Forgets everything. Called when a session ends.</summary>
        internal static void Reset()
        {
            _creditTo = null;
            Credited = 0;
            Pardoned = 0;
            _installed.Clear();
        }

        /// <summary>
        /// Names the player the next health change on this thread should be credited to, or null to stop
        /// crediting.
        ///
        /// Set immediately around <c>Health.ChangeHealth</c> rather than passed through it, because the
        /// only route from there to the threat book is an event the game raises synchronously inside the
        /// call and hands no context of its own.
        /// </summary>
        /// <param name="attackerGuid">The attacker's save guid, or null.</param>
        internal static void CreditTo(string? attackerGuid)
        {
            _creditTo = attackerGuid;
        }

        /// <summary>
        /// Files the threat a reported blow earned.
        ///
        /// On the adapter rather than on the creature, because the two are not the same book. Every
        /// <c>AIActorAdapter</c> builds its own <c>AIHealthAdapter</c> over one shared <c>Health</c>, and
        /// the threat service listens to the one belonging to the adapter <c>AI_TargetBhvr</c> made for
        /// itself - not the one reachable through the actor's factory. Raising the event on whichever
        /// instance the game has just run reaches whoever is listening to that instance, which is exactly
        /// how a real blow arrives.
        ///
        /// <c>IsPlayer</c> is set for the same reason: the service scales a player's threat down by a fixed
        /// factor, and a teammate's blows should count for what the host's identical blow counts for.
        /// </summary>
        private static void OnDamageEventListenerPostfix(AIHealthAdapter __instance, HealthChangeArgs arg0)
        {
            try
            {
                if (_creditTo == null) return;
                if (__instance == null || arg0 == null) return;

                // The original already filed this one under a real attacker, so it is not a blow this mod
                // is standing in for.
                if (arg0.HasSource) return;

                int dealt = Math.Abs(arg0.ActualChange);
                if (dealt <= 0) return;

                __instance.DamagedEvent.InvokeEvent(new AIHealthChangeArgs
                {
                    EntityKey = _creditTo,
                    DamageValue = dealt,
                    IsPlayer = true,
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] filing the threat a reported blow earned failed: " + e);
            }
        }

        /// <summary>
        /// Makes the creature regard the attacker as somebody it is fighting.
        ///
        /// The two steps <c>Health.CheckEvents</c> takes for a blow that names its attacker, in the same
        /// order and on the same terms. Both are idempotent: the combat set is a set, and the hostility is
        /// added only when the creature is not already hostile - which it will be from the first blow on.
        ///
        /// Called after the health change rather than before, so a blow that was refused outright - a dead
        /// creature, a type that could not be resolved - starts no fight.
        /// </summary>
        /// <param name="enemy">The creature that was struck.</param>
        /// <param name="attacker">The remote player's body that struck it.</param>
        /// <param name="attackerGuid">That body's save guid, which is the key the combat set is kept in.</param>
        internal static void Aggro(Actor enemy, Actor attacker, string attackerGuid)
        {
            try
            {
                if (enemy == null || attacker == null) return;

                // By guid rather than through the attacker's AI factory, which is how the game reaches the
                // same set. A puppet's AI is deactivated at creation, and asking its factory for an actor
                // there is a route into lazy-loading state that a body which never streams has no business
                // in - while the set itself only ever holds guids.
                _oneAttacker.Clear();
                _oneAttacker.Add(attackerGuid);

                if (enemy.AIModule != null)
                {
                    enemy.AIModule.AddTargetsInCombat(_oneAttacker);
                }

                MakeHostile(enemy, attacker, attackerGuid);

                Health health = enemy.GetHealth();
                if (health != null)
                {
                    // What ReceivedDamageLastFrame answers, and several AI decisions ask it. Without this a
                    // creature struck only by a teammate believes nothing has touched it.
                    health.SetReceivedDamageLastFrame();
                }

                Credited++;

                if (!_reported)
                {
                    _reported = true;
                    MelonLogger.Msg("[Enemy] a creature was told who hit it when a teammate's blow arrived. " +
                        "Without this it takes the damage and keeps chasing whoever it was already fighting.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] giving a reported blow its standing failed: " + e);
            }
        }

        /// <summary>
        /// What <c>HealthBhvr_OnDmgGetAggro</c> does, for an attacker the creature was given no way to see.
        ///
        /// Only when the creature is not already hostile, which is the game's own guard and is what keeps
        /// this from stacking a decorator per blow.
        /// </summary>
        private static void MakeHostile(Actor enemy, Actor attacker, string attackerGuid)
        {
            IAlignment alignment = enemy.Alignment;
            if (alignment == null) return;

            IActorAlignmentModule module = enemy.AlignmentModule;
            if (module == null) return;

            IAlignmentArgs args = new AlignmentArgs(attacker).Cast<IAlignmentArgs>();
            if (alignment.CheckAlignment(args, AlignmentState.Enemy)) return;

            IAlignment installed = new EnemyAlignment(attacker).Cast<IAlignment>();
            module.AddDecoratorAlignment(installed);

            _installed.Add(new Hostility(enemy.Pointer, attackerGuid, installed));
        }

        /// <summary>
        /// Takes back the standing a player's blows earned with a creature, when the fight between them
        /// was a duel that has ended.
        ///
        /// The mirror of what a brawl does for its own alignment when it ends. The hostility installed
        /// here has no such owner, so a creature duelled by a teammate would stay hostile to their body
        /// for the rest of the session - beaten, stood back up, and attacked again for a fight that was
        /// already settled.
        ///
        /// Every body this class made the creature hostile to is pardoned, each by the guid its record
        /// carries.
        /// </summary>
        /// <param name="enemy">The creature.</param>
        /// <param name="attackerGuid">A body to take out of the combat set as well, or null.</param>
        /// <returns>How many hostilities were actually taken back.</returns>
        internal static int Pardon(Actor enemy, string? attackerGuid)
        {
            try
            {
                if (enemy == null) return 0;

                IActorAlignmentModule module = enemy.AlignmentModule;
                AIActorModule? ai = enemy.AIModule?.TryCast<AIActorModule>();

                int removed = 0;

                for (int index = _installed.Count - 1; index >= 0; index--)
                {
                    Hostility hostility = _installed[index];
                    if (hostility.Enemy != enemy.Pointer) continue;

                    module?.RemoveAlignment(hostility.Alignment);

                    // Out of the combat set by hand, because the game only prunes it on the creature's
                    // own knockout recovery - and this creature may never have been knocked out.
                    ai?._actorGuidsInCombat?.Remove(hostility.AttackerGuid);

                    _installed.RemoveAt(index);
                    removed++;
                }

                if (attackerGuid != null)
                {
                    // Also the named body, which can be in the combat set without a hostility of its
                    // own: the standing was granted when the creature already counted it an enemy.
                    ai?._actorGuidsInCombat?.Remove(attackerGuid);
                }

                Pardoned += removed;

                // The count, not the fact. This used to report a pardon whether or not it had found
                // anything to take back, so a log line saying the duel was over proved only that the
                // message had arrived - which is how a creature that went on beating a teammate for the
                // rest of a session read as working.
                MelonLogger.Msg(removed > 0
                    ? $"[Engage] a creature was pardoned of a duel that ended; {removed} hostilit"
                        + (removed == 1 ? "y was" : "ies were") + " taken back."
                    : "[Engage] a creature was pardoned of a duel that ended, and this mod had installed "
                        + "no hostility on it to take back.");

                return removed;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Engage] pardoning a creature failed: " + e);
                return 0;
            }
        }

        /// <summary>
        /// Takes back every hostility this class installed on a creature, for whichever body it was
        /// installed for.
        ///
        /// For the end of a scripted brawl, and only for that. A brawl is the one fight in this game
        /// with an authored ending, and every hostility this class can have put on the creature during
        /// one was put there by a blow relayed into that same brawl - a teammate's swing, which arrives
        /// with no attacker of its own and is given this standing so that it counts for what a swing in
        /// person counts for. When the brawl resolves, the game takes its own alignments back through
        /// <c>BrawlActor.ResetBrawlEnemy</c> and cannot see ours, so ours are what remain: a settled
        /// duel leaves a creature permanently at war with a body that can be reached and cannot fight
        /// back, which is a teammate knocked down every few seconds for the rest of the session.
        ///
        /// Deliberately not called when an ordinary fight ends, because an ordinary fight does not end -
        /// a creature a teammate really is fighting should stay hostile to them exactly as it would if
        /// they were standing here in person.
        /// </summary>
        /// <param name="enemy">The creature whose duel is over.</param>
        /// <returns>How many hostilities were taken back.</returns>
        internal static int PardonAll(Actor enemy)
        {
            return Pardon(enemy, null);
        }
    }
}
