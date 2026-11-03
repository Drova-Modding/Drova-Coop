using Drova_Modding_API.Systems.MapEditor;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Session;
using MelonLoader;

namespace DrovaCoopMod.Systems.World
{
    /// <summary>
    /// Shares the use of objects the API's map layout places, so a trap sprung on one machine is gone on every machine.
    /// Pickups stay per player. Only <see cref="Loot.LootShare"/> shares them.
    /// </summary>
    internal sealed class MapConsumeShare : ICoopSystem
    {
        /// <summary>Consumptions applied from a teammate. For tests.</summary>
        internal static int Applied { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            MapLayoutSystem.OnConsumed += OnConsumed;
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<MapConsumedMessage>(MessageIds.MapConsumed, (peer, message) =>
            {
                if (CoopSession.HostsWorld) CoopSession.SendToClientsExcept(message, peer.Id);
                else if (!CoopSession.IsFromHost()) return;

                try
                {
                    MapLayoutSystem.MarkConsumed(message.Id);
                    Applied++;
                }
                catch (Exception e)
                {
                    MelonLogger.Error("[Map] removing a placed object a teammate used up failed: " + e);
                }
            }, worldOnly: true);
        }

        private static void OnConsumed(string id, bool pickup)
        {
            if (pickup || !CoopSession.SharesWorld) return;

            CoopSession.SendIfLive(new MapConsumedMessage { Id = id });
        }
    }
}
