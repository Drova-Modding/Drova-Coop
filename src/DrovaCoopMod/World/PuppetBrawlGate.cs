using Drova_Modding_API;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Brawl;
using Il2CppDrova.Brawl.Modules;
using Il2CppDrova.Cognition;
using Il2CppDrova.Cognition.Bhvrs;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Keeps remote players' bodies out of brawls.
    ///
    /// A puppet is cloned from the player prefab and answers the game's is-this-the-player tests, so
    /// the mine's capture bandits perceive it as a second catchable player and enroll it - by starting
    /// a brawl at it, or by pulling it in as a participant. A puppet can neither be knocked out by
    /// blows nor die, so a brawl holding one NEVER ends - and the capture is wired to the brawl's end
    /// (<c>Brawl_GVars._endOperations</c>, unconditional), which is how two players standing together
    /// made the bandits unable to capture anyone. The owner's real body fights its own copy of every
    /// scripted brawl on its own machine; the puppet has no business in this one.
    ///
    /// Gated at every enrollment door: the cognition trigger that starts capture brawls, the brawl
    /// start itself, and both participant-add paths. Counters are printed even at zero, because this
    /// build inlines small methods and a dead patch reads identically to a quiet world.
    /// </summary>
    internal static class PuppetBrawlGate
    {
        private static int _asked;
        private static int _refused;

        /// <summary>How many gate checks ran. Zero while brawls happen means a dead patch.</summary>
        internal static int Asked => _asked;

        /// <summary>How many brawl enrollments were refused for a puppet. For <c>coop_status</c>.</summary>
        internal static int Refused => _refused;

        /// <summary>Forgets the counts. Called when a session ends.</summary>
        internal static void Reset()
        {
            _asked = 0;
            _refused = 0;
        }

        /// <summary>Applies the patches. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(CognitionStartBrawl), "CognitionReceivedEventListener",
                typeof(PuppetBrawlGate), nameof(CognitionPrefix));
            Hooking.TryPrefix(harmony, typeof(BrawlController), "StartBrawl",
                typeof(PuppetBrawlGate), nameof(StartBrawlPrefix));
            Hooking.TryPrefix(harmony, typeof(BrawlController), "AddStarterParticipant",
                typeof(PuppetBrawlGate), nameof(AddStarterPrefix));
            Hooking.TryPrefix(harmony, typeof(BrawlController), "AddReceiverParticipant",
                typeof(PuppetBrawlGate), nameof(AddReceiverPrefix));
        }

        /// <summary>
        /// Stops a capture-style cognition trigger from even building a brawl at a puppet. The later
        /// gates would refuse the enrollment anyway, but this one runs before a controller is
        /// instantiated, so a puppet standing in the ambush does not mint a dead brawl every retry.
        /// </summary>
        private static bool CognitionPrefix(ICognitionObject arg0)
        {
            try
            {
                _asked++;

                if (arg0 == null) return true;

                Actor? actor = arg0.GetCognitionArgs()?.Actor;
                if (actor == null || !RemotePlayers.IsPuppet(actor)) return true;

                Refuse();
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Brawl] gating a cognition brawl start failed: " + e);
                return true;
            }
        }

        private static bool StartBrawlPrefix(Actor startActor, Actor receiverActor)
        {
            try
            {
                _asked++;

                if (!IsPuppet(startActor) && !IsPuppet(receiverActor)) return true;

                Refuse();
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Brawl] gating a brawl start failed: " + e);
                return true;
            }
        }

        private static bool AddStarterPrefix(BrawlActor brawlActor)
        {
            return AllowParticipant(brawlActor);
        }

        private static bool AddReceiverPrefix(BrawlActor brawlActor)
        {
            return AllowParticipant(brawlActor);
        }

        private static bool AllowParticipant(BrawlActor? brawlActor)
        {
            try
            {
                _asked++;

                Actor? actor = brawlActor?.GetActor();
                if (actor == null || !RemotePlayers.IsPuppet(actor)) return true;

                Refuse();
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Brawl] gating a brawl participant failed: " + e);
                return true;
            }
        }

        private static bool IsPuppet(Actor? actor)
        {
            return actor != null && RemotePlayers.IsPuppet(actor);
        }

        private static void Refuse()
        {
            _refused++;

            if (_refused == 1)
            {
                MelonLogger.Msg("[Brawl] a remote player's body was kept out of a brawl. Its owner " +
                    "fights their own copy of that fight; a participant no blow can fell would keep " +
                    "the brawl from ever ending.");
            }
        }
    }
}
