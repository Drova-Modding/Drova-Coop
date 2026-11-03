using Il2CppDrova;
using Il2CppDrova.ArenaSystem;
using Il2CppDrova.Brawl;
using Il2CppDrova.Brawl.Modules;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DrovaCoopMod.Systems.GameArena
{
    /// <summary>
    /// Puts a teammate who strikes a fighter of the arena fight or of a scripted brawl into that fight, on the player's side.
    /// Their knockout comes from their own machine, so it is told to the fight in <see cref="KnockedDown"/>.
    /// </summary>
    internal sealed class ArenaTeammates : ICoopSystem
    {
        /// <summary>Teammates still on their feet in a fight, by body.</summary>
        private static readonly Dictionary<IntPtr, BrawlActor> Standing = new();

        /// <summary>
        /// Every teammate who joined a fight still running, downed ones too, since the brawl aborts if a listed body is destroyed.
        /// A downed one may not rejoin, or a side whose players all went down would never lose.
        /// </summary>
        private static readonly Dictionary<IntPtr, (BrawlActor Fighter, BrawlController Fight)> Fought = new();

        private static readonly List<IntPtr> Finished = new();
        private static DAS_ArenaSystem? _arena;
        private static bool _noFightSaid;

        /// <summary>Whether a join is under way, so PuppetBrawlGate lets this one body in.</summary>
        internal static bool Admitting { get; private set; }

        /// <summary>Teammates who joined a fight here. For tests.</summary>
        internal static int Joined { get; private set; }

        /// <summary>Teammates knocked out of one. For tests.</summary>
        internal static int KnockedOut { get; private set; }

        /// <summary>Whether this teammate was knocked out of a fight still running here. Their blows no longer count in it.</summary>
        internal static bool IsOut(Actor? teammate)
            => teammate != null && Fought.ContainsKey(teammate.Pointer) && !Standing.ContainsKey(teammate.Pointer);

        /// <summary>A teammate's blow landed on <paramref name="struck"/>, so their body joins the fight it is in.</summary>
        internal static void TryJoin(Actor struck, Actor? teammate)
        {
            try
            {
                if (teammate == null || Fought.ContainsKey(teammate.Pointer)) return;
                if (struck.CustomStats == null || !struck.CustomStats.IsInBrawl || teammate.CustomStats.IsKnockout) return;

                bool arena = ArenaGates.AtTheArena(struck.GetFeetPosition());
                if (arena && !ArenaGates.RunsTheFight()) return;

                // The game's own join-by-hitting refuses a duel, but a teammate is the player's second.
                BrawlController? fight = arena ? ArenaFight() : BrawlOf(struck);
                if (fight == null)
                {
                    if (!_noFightSaid) MelonLogger.Warning("[Brawl] a teammate struck a fighter, but no fight was found running for it.");
                    _noFightSaid = true;
                    return;
                }

                BrawlService? service = fight._brawlService?.TryCast<BrawlService>();
                bool asStarter = arena || (service != null && service.IsReceiverActor(struck));

                BrawlActor fighter = new(teammate);
                Admitting = true;
                try
                {
                    if (asStarter) fight.AddStarterParticipant(fighter);
                    else fight.AddReceiverParticipant(fighter);
                }
                finally
                {
                    Admitting = false;
                }

                Standing[teammate.Pointer] = fighter;
                Fought[teammate.Pointer] = (fighter, fight);
                Joined++;
                MelonLogger.Msg("[Brawl] a teammate struck a fighter and fights at the player's side now.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Brawl] taking a teammate into the fight failed: " + e);
            }
        }

        /// <summary>A teammate's body went down and leaves the fight, which may end it.</summary>
        internal static void KnockedDown(Actor body)
        {
            try
            {
                if (!Standing.Remove(body.Pointer, out BrawlActor fighter)) return;

                // BrawlService takes a fighter out on a health drop while knocked out, and the mirrored drop may come first.
                Health? health = body.GetHealth();
                fighter.HealthChangedEvent.InvokeEvent(new Il2CppSystem.ValueTuple<int, int>(health != null ? health.CurHealth : 0, health != null ? health.MaxHealth : 1));
                KnockedOut++;
                MelonLogger.Msg("[Brawl] a teammate went down and is out of the fight.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Brawl] taking a downed teammate out of the fight failed: " + e);
            }
        }

        /// <summary>Takes a body about to be destroyed out of the fight, since a missing participant aborts it.</summary>
        internal static void Leave(Actor body)
        {
            try
            {
                Standing.Remove(body.Pointer);
                if (!Fought.Remove(body.Pointer, out (BrawlActor Fighter, BrawlController Fight) entry)) return;

                BrawlService? service = entry.Fight == null ? null : entry.Fight._brawlService?.TryCast<BrawlService>();
                if (service == null) return;

                IBrawlActor fighter = entry.Fighter.Cast<IBrawlActor>();
                service._starterActors.Remove(fighter);
                service._starterFaction.Remove(fighter);
                service._receiverActors.Remove(fighter);
                service._receiverFaction.Remove(fighter);
                MelonLogger.Msg("[Brawl] a teammate left; their body was taken out of the fight first.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Brawl] taking a leaving teammate out of the fight failed: " + e);
            }
        }

        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (Fought.Count > 0) ForgetFinished();
        }

        private static void ForgetFinished()
        {
            Finished.Clear();
            foreach (KeyValuePair<IntPtr, (BrawlActor Fighter, BrawlController Fight)> entry in Fought)
            {
                BrawlController fight = entry.Value.Fight;
                if (fight == null || fight._brawlFinished) Finished.Add(entry.Key);
            }

            foreach (IntPtr body in Finished)
            {
                Fought.Remove(body);
                Standing.Remove(body);
            }

            if (Fought.Count == 0) _arena = null;
        }

        /// <summary>The scripted brawl a fighter is in. Mass brawls run another service and are not joined.</summary>
        private static BrawlController? BrawlOf(Actor struck)
        {
            foreach (BrawlController brawl in Object.FindObjectsOfType<BrawlController>())
            {
                if (brawl == null || brawl._brawlFinished) continue;

                BrawlService? service = brawl._brawlService?.TryCast<BrawlService>();
                if (service != null && service._starterActor != null && service._receiverActor != null
                    && (service.IsStarterActor(struck) || service.IsReceiverActor(struck)))
                {
                    return brawl;
                }
            }

            return null;
        }

        private static BrawlController? ArenaFight()
        {
            BrawlController? cached = _arena == null ? null : _arena._arenaBrawl?._activeBrawl;
            if (cached != null) return cached;

            foreach (DAS_ArenaSystem arena in Resources.FindObjectsOfTypeAll<DAS_ArenaSystem>())
            {
                BrawlController? active = arena == null ? null : arena._arenaBrawl?._activeBrawl;
                if (active == null) continue;

                _arena = arena;
                return active;
            }

            return null;
        }

        /// <summary>Kept while a fight runs, since Leave needs its bodies when they are destroyed after this.</summary>
        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings) => ForgetFinished();
    }
}
