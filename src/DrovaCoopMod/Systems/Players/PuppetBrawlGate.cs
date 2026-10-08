using Drova_Modding_API;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Players.Patches;
using Il2CppDrova;
using Il2CppDrova.Brawl;
using Il2CppDrova.Brawl.Modules;
using Il2CppDrova.Cognition;
using Il2CppDrova.Cognition.Bhvrs;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Keeps remote players' bodies out of brawls. A puppet cannot be knocked out, so a brawl holding one
    /// never ends, and captures wait on <c>Brawl_GVars._endOperations</c>.
    /// </summary>
    internal sealed class PuppetBrawlGate : ICoopSystem
    {
        /// <summary>How many gate checks ran. Zero while brawls happen means a dead patch.</summary>
        internal static int Asked { get; private set; }

        /// <summary>Brawl enrollments refused for a puppet.</summary>
        internal static int Refused { get; private set; }

        private static void Reset()
        {
            Asked = 0;
            Refused = 0;
        }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPrefix(harmony, typeof(CognitionStartBrawl), "CognitionReceivedEventListener",
                typeof(PuppetBrawlGatePatches), nameof(PuppetBrawlGatePatches.CognitionPrefix));
            Hooking.TryPrefix(harmony, typeof(BrawlController), "StartBrawl",
                typeof(PuppetBrawlGatePatches), nameof(PuppetBrawlGatePatches.StartBrawlPrefix));
            Hooking.TryPrefix(harmony, typeof(BrawlController), "AddStarterParticipant",
                typeof(PuppetBrawlGatePatches), nameof(PuppetBrawlGatePatches.AddStarterPrefix));
            Hooking.TryPrefix(harmony, typeof(BrawlController), "AddReceiverParticipant",
                typeof(PuppetBrawlGatePatches), nameof(PuppetBrawlGatePatches.AddReceiverPrefix));
        }

        /// <summary>Runs before a controller is built, so a puppet in an ambush does not mint a dead brawl every retry.</summary>
        internal static bool CognitionPrefix(ICognitionObject? arg0)
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

        internal static bool StartBrawlPrefix(Actor startActor, Actor receiverActor)
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

        internal static bool AddStarterPrefix(BrawlActor brawlActor)
        {
            return AllowParticipant(brawlActor);
        }

        internal static bool AddReceiverPrefix(BrawlActor brawlActor)
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

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
