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
    /// Keeps remote players' bodies out of brawls. A puppet cannot be knocked out, so a brawl holding one
    /// never ends, and captures wait on <c>Brawl_GVars._endOperations</c>.
    /// </summary>
    internal static class PuppetBrawlGate
    {
        /// <summary>How many gate checks ran. Zero while brawls happen means a dead patch.</summary>
        internal static int Asked { get; private set; }

        /// <summary>Brawl enrollments refused for a puppet.</summary>
        internal static int Refused { get; private set; }

        /// <summary>Called when a session ends.</summary>
        internal static void Reset()
        {
            Asked = 0;
            Refused = 0;
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

        /// <summary>Runs before a controller is built, so a puppet in an ambush does not mint a dead brawl every retry.</summary>
        private static bool CognitionPrefix(ICognitionObject arg0)
        {
            try
            {
                Asked++;

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
                Asked++;

                if (!RemotePlayers.IsPuppet(startActor) && !RemotePlayers.IsPuppet(receiverActor)) return true;

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
                Asked++;

                Actor? actor = brawlActor?.GetActor();
                if (actor == null || !RemotePlayers.IsPuppet(actor) || ArenaTeammates.Admitting) return true;

                Refuse();
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Brawl] gating a brawl participant failed: " + e);
                return true;
            }
        }

        private static void Refuse()
        {
            Refused++;

            if (Refused == 1)
            {
                MelonLogger.Msg("[Brawl] a remote player's body was kept out of a brawl. Its owner " +
                    "fights their own copy of that fight; a participant no blow can fell would keep " +
                    "the brawl from ever ending.");
            }
        }
    }
}
