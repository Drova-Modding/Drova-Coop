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

        private static string? _creditTo;
        private static int _credited;
        private static bool _reported;

        /// <summary>
        /// How many reported blows were given standing with the creature they hit. For <c>coop_status</c>.
        /// </summary>
        internal static int Credited => _credited;

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
            _credited = 0;
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

                MakeHostile(enemy, attacker);

                Health health = enemy.GetHealth();
                if (health != null)
                {
                    // What ReceivedDamageLastFrame answers, and several AI decisions ask it. Without this a
                    // creature struck only by a teammate believes nothing has touched it.
                    health.SetReceivedDamageLastFrame();
                }

                _credited++;

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
        private static void MakeHostile(Actor enemy, Actor attacker)
        {
            IAlignment alignment = enemy.Alignment;
            if (alignment == null) return;

            IActorAlignmentModule module = enemy.AlignmentModule;
            if (module == null) return;

            IAlignmentArgs args = new AlignmentArgs(attacker).Cast<IAlignmentArgs>();
            if (alignment.CheckAlignment(args, AlignmentState.Enemy)) return;

            module.AddDecoratorAlignment(new EnemyAlignment(attacker).Cast<IAlignment>());
        }
    }
}
