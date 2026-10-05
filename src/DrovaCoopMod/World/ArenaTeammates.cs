using Il2CppDrova;
using Il2CppDrova.ArenaSystem;
using Il2CppDrova.Brawl;
using Il2CppDrova.Brawl.Modules;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// A teammate fighting beside the player in the game's arena is in the fight: on the player's side, a target the
    /// champion can turn to, and out of it when they go down - the fight goes on until the champion or the whole side falls.
    ///
    /// Joined on the machine running the fight, by the teammate's first blow on a fighter there (a watching teammate stays
    /// out). Possible only because a teammate's body has its own EntityInfo (PuppetFactory): carrying the player's, it was
    /// the player to the brawl's winners and the arena's teams, and the duel ended as the player's win within seconds.
    /// A teammate's body is never knocked out by blows here (their health is their own machine's), so their going down is
    /// told to the fight when it arrives (<see cref="KnockedDown"/>).
    /// </summary>
    internal static class ArenaTeammates
    {
        // Teammates' bodies in the running fight and still standing, by body; and every one that joined it, down or not -
        // the brawl keeps a knocked-out starter on its list, and aborts the fight if that body is destroyed.
        private static readonly Dictionary<IntPtr, BrawlActor> _joined = new();
        private static readonly Dictionary<IntPtr, BrawlActor> _fought = new();
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
                if (teammate == null || _joined.ContainsKey(teammate.Pointer)) return;
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

                _joined[teammate.Pointer] = fighter;
                _fought[teammate.Pointer] = fighter;
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
                if (!_joined.Remove(body.Pointer, out BrawlActor? fighter)) return;

                // The brawl takes a fighter out on a health drop while it lies knocked out (BrawlService); the drop of a
                // body whose health is mirrored may come before or after, so the fight is told now.
                Health health = body.GetHealth();
                fighter.HealthChangedEvent.InvokeEvent(new Il2CppSystem.ValueTuple<int, int>(health?.CurHealth ?? 0, health?.MaxHealth ?? 1));
                KnockedOut++;
                MelonLogger.Msg("[Arena] a teammate went down and is out of the arena fight.");
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Arena] taking a downed teammate out of the arena fight failed: " + e);
            }
        }

        /// <summary>
        /// A teammate's body is about to be destroyed (they left, the session or world ended): out of the fight's lists. The
        /// brawl aborts when a starter's body is gone, and an abort is the player's loss.
        /// </summary>
        internal static void Leave(Actor body)
        {
            try
            {
                _joined.Remove(body.Pointer);
                if (!_fought.Remove(body.Pointer, out BrawlActor? fighter)) return;

                BrawlService? service = ActiveFight()?._brawlService?.TryCast<BrawlService>();
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

        /// <summary>Forgets a finished fight. Once per frame.</summary>
        internal static void Update()
        {
            if (_fought.Count > 0 && ActiveFight() == null)
            {
                _joined.Clear();
                _fought.Clear();
            }
        }

        /// <summary>Forgets everything. Called when a world or session ends.</summary>
        internal static void Clear()
        {
            // Kept while the fight still runs (a session ending in the same world): the bodies are destroyed after this,
            // and Leave needs them listed. Update forgets them when the fight is over.
            if (ActiveFight() != null) return;

            _joined.Clear();
            _fought.Clear();
            _arena = null;
        }

        private static BrawlController? ActiveFight()
        {
            if (_arena?._arenaBrawl?._activeBrawl is { } cached) return cached;

            // Whichever arena system is running a fight (there may be more than one, and inactive ones).
            foreach (DAS_ArenaSystem arena in UnityEngine.Resources.FindObjectsOfTypeAll<DAS_ArenaSystem>())
            {
                if (arena?._arenaBrawl?._activeBrawl == null) continue;

                _arena = arena;
                return arena._arenaBrawl._activeBrawl;
            }

            return null;
        }
    }
}
