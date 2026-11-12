using Drova_Modding_API;
using DrovaCoopMod.Systems.Enemies.Patches;
using Il2CppDrova;
using Il2CppDrova.AI.Modules;
using Il2CppDrova.Alignment;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Gives a client's relayed blow the threat, combat tag and hostility a blow in person would have carried.
    /// <see cref="RemoteEnemyHealth"/> applies it sourceless, so the game itself files none of these.
    /// </summary>
    internal sealed class EnemyThreat : ICoopSystem
    {
        private static readonly Il2CppSystem.Collections.Generic.HashSet<string> OneAttacker = new();

        /// <summary>Hostilities installed here. No game code removes them, so <see cref="Pardon"/> does.</summary>
        private static readonly List<Hostility> Installed = new();

        private sealed record Hostility(IntPtr Enemy, string AttackerGuid, IAlignment Alignment);

        private static string? _creditTo;
        private static bool _reported;

        /// <summary>Hostilities taken back after a duel ended.</summary>
        internal static int Pardoned { get; private set; }

        /// <summary>Relayed blows given standing with their target.</summary>
        internal static int Credited { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(AIHealthAdapter), "OnDamageEventListener",
                typeof(EnemyThreatPatches), nameof(EnemyThreatPatches.OnDamageEventListenerPostfix));
        }

        internal static void Reset()
        {
            _creditTo = null;
            Credited = 0;
            Pardoned = 0;
            Installed.Clear();
        }

        /// <summary>
        /// Names the player the next health change on this thread is credited to, or null to stop.
        /// Set around <c>Health.ChangeHealth</c>, whose damage event is raised synchronously with no context.
        /// </summary>
        internal static void CreditTo(string? attackerGuid)
        {
            _creditTo = attackerGuid;
        }

        /// <summary>
        /// Files the threat a relayed blow earned.
        /// Hooked on the adapter because the threat service listens only to the one <c>AI_TargetBhvr</c> made.
        /// </summary>
        internal static void OnDamageEventListenerPostfix(AIHealthAdapter __instance, HealthChangeArgs? arg0)
        {
            try
            {
                if (_creditTo == null) return;
                if (arg0 == null) return;

                // Already filed under a real attacker.
                if (arg0.HasSource) return;

                int dealt = Math.Abs(arg0.ActualChange);
                if (dealt <= 0) return;

                // IsPlayer so the service applies the same player threat scaling as a local blow.
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
        /// Files the threat of a blow that was counted on another machine, so no health change here carries it.
        /// Raised on the creature's own damage event, which is what its threat service listens to.
        /// </summary>
        internal static void File(Actor enemy, string attackerGuid, int amount)
        {
            try
            {
                enemy.AIModule?.GetIAIFactory()?.GetActor()?.GetHealth()?.DamagedEvent?.InvokeEvent(new AIHealthChangeArgs
                {
                    EntityKey = attackerGuid,
                    DamageValue = amount,
                    IsPlayer = true,
                });
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] filing the threat of a blow counted elsewhere failed: " + e);
            }
        }

        /// <summary>
        /// Makes the creature fight the attacker, as <c>Health.CheckEvents</c> does for a sourced blow.
        /// Called after the health change, so a refused blow starts no fight.
        /// </summary>
        internal static void Aggro(Actor enemy, Actor attacker, string attackerGuid)
        {
            try
            {
                if (enemy == null || attacker == null) return;

                // By guid, since a puppet's AI is deactivated and never streams.
                OneAttacker.Clear();
                OneAttacker.Add(attackerGuid);

                if (enemy.AIModule != null)
                {
                    enemy.AIModule.AddTargetsInCombat(OneAttacker);
                }

                MakeHostile(enemy, attacker, attackerGuid);

                Health health = enemy.GetHealth();
                if (health != null)
                {
                    // Several AI decisions ask ReceivedDamageLastFrame.
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

        /// <summary>What <c>HealthBhvr_OnDmgGetAggro</c> does, guarded the same way so decorators do not stack per blow.</summary>
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

            Installed.Add(new Hostility(enemy.Pointer, attackerGuid, installed));
        }

        /// <summary>Takes back the hostilities this class installed on a creature once their duel ended.</summary>
        /// <returns>How many hostilities were taken back.</returns>
        internal static int Pardon(Actor enemy, string? attackerGuid)
        {
            try
            {
                if (enemy == null) return 0;

                IActorAlignmentModule module = enemy.AlignmentModule;
                AIActorModule? ai = enemy.AIModule?.TryCast<AIActorModule>();

                int removed = 0;

                for (int index = Installed.Count - 1; index >= 0; index--)
                {
                    Hostility hostility = Installed[index];
                    if (hostility.Enemy != enemy.Pointer) continue;

                    module?.RemoveAlignment(hostility.Alignment);

                    // The game only prunes the combat set on the creature's own knockout recovery.
                    ai?._actorGuidsInCombat?.Remove(hostility.AttackerGuid);

                    Installed.RemoveAt(index);
                    removed++;
                }

                if (attackerGuid != null)
                {
                    // Can be in the combat set without a hostility of its own.
                    ai?._actorGuidsInCombat?.Remove(attackerGuid);
                }

                Pardoned += removed;

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
        /// Takes back every hostility installed on a creature when a scripted brawl ends.
        /// <c>BrawlActor.ResetBrawlEnemy</c> removes only the game's own. Not for ordinary fights.
        /// </summary>
        /// <returns>How many hostilities were taken back.</returns>
        internal static int PardonAll(Actor enemy)
        {
            return Pardon(enemy, null);
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
