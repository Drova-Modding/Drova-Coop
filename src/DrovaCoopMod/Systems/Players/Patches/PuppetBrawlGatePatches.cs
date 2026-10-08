using Il2CppDrova;
using Il2CppDrova.Brawl;
using Il2CppDrova.Cognition;

namespace DrovaCoopMod.Systems.Players.Patches
{
    /// <summary>The Harmony entry points of <see cref="PuppetBrawlGate"/>. Each forwards to the system.</summary>
    internal static class PuppetBrawlGatePatches
    {
        internal static bool CognitionPrefix(ICognitionObject arg0)
            => PuppetBrawlGate.CognitionPrefix(arg0);

        internal static bool StartBrawlPrefix(Actor startActor, Actor receiverActor)
            => PuppetBrawlGate.StartBrawlPrefix(startActor, receiverActor);

        internal static bool AddStarterPrefix(BrawlActor brawlActor)
            => PuppetBrawlGate.AddStarterPrefix(brawlActor);

        internal static bool AddReceiverPrefix(BrawlActor brawlActor)
            => PuppetBrawlGate.AddReceiverPrefix(brawlActor);
    }
}
