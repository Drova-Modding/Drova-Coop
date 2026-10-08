using Il2CppDrova;
using Il2CppDrova.ArenaSystem;
using Il2CppDrova.Brawl;
using Il2CppDrova.Brawl.Modules;
using MelonLoader;

namespace DrovaCoopMod.Systems.GameArena
{
    /// <summary>
    /// Puts a teammate who strikes an arena fighter into the game arena fight, on the player's side.
    /// Their knockout comes from their own machine, so it is told to the fight in <see cref="KnockedDown"/>.
    /// </summary>
    internal sealed class ArenaTeammates : ICoopSystem
    {
        // Fought keeps downed bodies too, since the brawl aborts if a listed starter's body is destroyed.
        private static readonly Dictionary<IntPtr, BrawlActor> Standing = new();
        private static readonly Dictionary<IntPtr, BrawlActor> Fought = new();
        private static DAS_ArenaSystem? _arena;
        private static bool _noFightSaid;

        /// <summary>Whether a join is under way, so PuppetBrawlGate lets this one body in.</summary>
        internal static bool Admitting { get; private set; }

        /// <summary>Teammates who joined an arena fight here. For tests.</summary>
        internal static int Joined { get; private set; }

        /// <summary>Teammates knocked out of one. For tests.</summary>
        internal static int KnockedOut { get; private set; }

        /// <summary>A teammate's blow landed on <paramref name="struck"/>: their body joins the arena fight it is in.</summary>
        internal static void TryJoin(Actor struck, Actor? teammate)
        {
            try
            {
                if (teammate == null || Standing.ContainsKey(teammate.Pointer)) return;
                if (struck.CustomStats == null || !struck.CustomStats.IsInBrawl || teammate.CustomStats.IsKnockout) return;
                if (!ArenaGates.RunsTheFight() || !ArenaGates.AtTheArena(struck.GetFeetPosition())) return;

                BrawlController? fight = ActiveFight();
                if (fight == null)
                {
                    if (!_noFightSaid) MelonLogger.Warning("[Arena] a teammate struck an arena fighter, but no arena fight was found running.");
                    _noFightSaid = true;
                    return;
                }

                BrawlActor fighter = new(teammate);
                Admitting = true;
                try
                {
                    fight.AddStarterParticipant(fighter);
                }
                finally
                {
                    Admitting = false;
                }

                Standing[teammate.Pointer] = fighter;
                Fought[teammate.Pointer] = fighter;
                Joined++;
                MelonLogger.Msg("[Arena] a teammate struck the champion and fights at the player's side now.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] taking a teammate into the arena fight failed: " + e);
            }
        }

        /// <summary>A teammate's body went down: out of the fight, which may end it.</summary>
        internal static void KnockedDown(Actor body)
        {
            try
            {
                if (!Standing.Remove(body.Pointer, out BrawlActor fighter)) return;

                // BrawlService takes a fighter out on a health drop while knocked out, and the mirrored drop may come first.
                Health? health = body.GetHealth();
                fighter.HealthChangedEvent.InvokeEvent(new Il2CppSystem.ValueTuple<int, int>(health != null ? health.CurHealth : 0, health != null ? health.MaxHealth : 1));
                KnockedOut++;
                MelonLogger.Msg("[Arena] a teammate went down and is out of the arena fight.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] taking a downed teammate out of the arena fight failed: " + e);
            }
        }

        /// <summary>Takes a body about to be destroyed out of the fight, since a missing starter aborts it as the player's loss.</summary>
        internal static void Leave(Actor body)
        {
            try
            {
                Standing.Remove(body.Pointer);
                if (!Fought.Remove(body.Pointer, out BrawlActor fighter)) return;

                BrawlController? fight = ActiveFight();
                BrawlService? service = fight == null ? null : fight._brawlService?.TryCast<BrawlService>();
                if (service == null) return;

                IBrawlActor entry = fighter.Cast<IBrawlActor>();
                service._starterActors.Remove(entry);
                service._starterFaction.Remove(entry);
                MelonLogger.Msg("[Arena] a teammate left; their body was taken out of the arena fight first.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] taking a leaving teammate out of the arena fight failed: " + e);
            }
        }

        /// <summary>Forgets a finished fight.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (Fought.Count > 0 && ActiveFight() == null)
            {
                Standing.Clear();
                Fought.Clear();
            }
        }

        /// <summary>Forgets everything.</summary>
        private static void Clear()
        {
            // Kept while the fight runs, since Leave needs them when the bodies are destroyed after this.
            if (ActiveFight() != null) return;

            Standing.Clear();
            Fought.Clear();
            _arena = null;
        }

        private static BrawlController? ActiveFight()
        {
            BrawlController? cached = _arena == null ? null : _arena._arenaBrawl?._activeBrawl;
            if (cached != null) return cached;

            foreach (DAS_ArenaSystem arena in UnityEngine.Resources.FindObjectsOfTypeAll<DAS_ArenaSystem>())
            {
                BrawlController? active = arena == null ? null : arena._arenaBrawl?._activeBrawl;
                if (active == null) continue;

                _arena = arena;
                return active;
            }

            return null;
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => Clear();
    }
}
