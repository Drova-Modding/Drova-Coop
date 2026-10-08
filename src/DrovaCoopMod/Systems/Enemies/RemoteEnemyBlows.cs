using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Enemies.Patches;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.HitSystem;
using MelonLoader;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Stops a host-driven creature's replayed swing from also hitting this client's player, since the host reports that blow.
    /// Refused at the hit filter so stagger, knockback and poison are refused too.
    /// </summary>
    internal sealed class RemoteEnemyBlows : ICoopSystem
    {
        private static bool _reported;

        /// <summary>Blows from host-driven creatures discarded here. For <c>coop_status</c>.</summary>
        internal static int Discarded { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // Hit entry and hit update both pass through here with the built arguments in hand.
            Hooking.TryPostfix(harmony, typeof(HitReceiveHandler), "CheckFilterWithBhvrs",
                typeof(RemoteEnemyBlowsPatches), nameof(RemoteEnemyBlowsPatches.CheckFilterWithBhvrsPostfix));
        }

        /// <summary>Forgets the count. Called when a session ends.</summary>
        private static void Reset()
        {
            Discarded = 0;
        }

        /// <summary>Turns an accepted hit into a refused one when the host has already answered for it.</summary>
        internal static void CheckFilterWithBhvrsPostfix(HitReceiveHandler __instance, HitTriggerArgs? args,
            ref bool __result)
        {
            try
            {
                if (!__result) return;

                if (args == null || !HostAnswers(args.Entity)) return;

                if (!IsTheLocalPlayer(__instance)) return;

                __result = false;
                Discarded++;

                if (!_reported)
                {
                    _reported = true;
                    MelonLogger.Msg("[Enemy] a blow from a creature the other player drives was discarded " +
                        "here. Their game is the one that answers for it, and letting this copy connect as " +
                        "well would charge for the same attack twice.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] deciding whether a driven creature's blow lands failed: " + e);
            }
        }

        /// <summary>
        /// Client: whether a blow from this source is the host's to report, so it must not land here as well.
        /// Asked at the hit filter and at the damage funnel (DownedState), since area attacks skip the filter.
        /// </summary>
        internal static bool HostAnswers(Entity? source)
        {
            if (CoopSession.State != CoopSessionState.Joined || source == null) return false;

            // A boss strikes through a hit entity of its own (the Great Tusk), not its Actor.
            Actor? striker = source.TryCast<Actor>() ?? source.GetComponentInParent<Actor>();
            if (striker == null) return false;

            return EnemyRegistry.TryGetId(striker, out EnemyNetId id) && EnemyRegistry.IsDriven(id);
        }

        /// <summary>Whether this receiver belongs to the local player, who carries several receivers.</summary>
        private static bool IsTheLocalPlayer(HitReceiveHandler receiver)
        {
            return RemotePlayers.IsLocalPlayer(receiver.GetComponentInParent<Actor>());
        }

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            if (!loaded) Reset();
        }
    }
}
