using Drova_Modding_API;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players.Patches;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.ABI;
using Il2CppDrova.GUI;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// The mole's dirt throw dirties the HUD of whoever it hits, gated only on <c>IsPlayer</c>, which puppets are.
    /// A hit on a puppet is moved to its owner's screen.
    /// </summary>
    internal sealed class PuppetDirtBlind : ICoopSystem
    {
        /// <summary>Dirt throws on a puppet sent to its owner. For tests.</summary>
        internal static int Relayed { get; private set; }

        /// <summary>Dirt throws a teammate's host reported on this player. For tests.</summary>
        internal static int Shown { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // The only caller of GUI_HUD.TriggerDirtEffect.
            Hooking.TryPrefix(harmony, typeof(ABI_Bhvr_HitEffect_DirtBlind), "OnHit",
                typeof(PuppetDirtBlindPatches), nameof(PuppetDirtBlindPatches.OnHitPrefix));
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<DirtBlindMessage>(MessageIds.DirtBlind, OnDirtBlind);
        }

        /// <returns>False for a puppet, so the dirt never reaches this machine's HUD.</returns>
        internal static bool OnHitPrefix(Entity? actor)
        {
            try
            {
                if (!RemotePlayers.TryFindOwner(actor, out PlayerId owner)) return true;

                // ponytail: host only, since creatures run there. A client-leased fight is a brawl, never a mole.
                if (CoopSession.State == CoopSessionState.Hosting && CoopSession.TrySendToPlayer(new DirtBlindMessage(), owner))
                {
                    Relayed++;
                }

                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Puppet] moving a dirt throw to its owner's screen failed: " + e);
                return true;
            }
        }

        private static void OnDirtBlind(INetPeer peer, DirtBlindMessage message)
        {
            if (CoopSession.State != CoopSessionState.Joined || !CoopSession.IsFromHost()) return;

            if (!GUIGameHandler.TryGet(out GUIGameHandler gui) || gui == null || gui._guiHudInstance == null) return;

            gui._guiHudInstance.TriggerDirtEffect();
            Shown++;
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (loaded) return;

            Relayed = 0;
            Shown = 0;
        }
    }
}
