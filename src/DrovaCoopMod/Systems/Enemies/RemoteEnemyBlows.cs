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
    /// A host-driven creature's replayed swing lands on this client's player where it connects here, so damage, dodge,
    /// block and poise are judged against the real body without a round trip. Only a teammate's scripted brawl is
    /// refused, since its blow must knock out rather than down and only the host's report says so.
    /// Refused at the hit filter so stagger, knockback and poison are refused too.
    /// </summary>
    internal sealed class RemoteEnemyBlows : ICoopSystem
    {
        private static bool _reported;

        /// <summary>Blows from a teammate's brawl discarded here. For <c>coop_status</c>.</summary>
        internal static int Discarded { get; private set; }

        /// <summary>Blows from host-driven creatures that landed here. For tests.</summary>
        internal static int Landed { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            // Hit entry and hit update both pass through here with the built arguments in hand.
            Hooking.TryPostfix(harmony, typeof(HitReceiveHandler), "CheckFilterWithBhvrs",
                typeof(RemoteEnemyBlowsPatches), nameof(RemoteEnemyBlowsPatches.CheckFilterWithBhvrsPostfix));
        }

        private static void Reset()
        {
            Discarded = 0;
            Landed = 0;
        }

        /// <summary>Turns an accepted hit into a refused one when the host answers for it.</summary>
        internal static void CheckFilterWithBhvrsPostfix(HitReceiveHandler __instance, HitTriggerArgs? args,
            ref bool __result)
        {
            try
            {
                if (!__result || args == null || !IsDrivenStriker(args.Entity, out EnemyNetId id)) return;

                if (!IsTheLocalPlayer(__instance)) return;

                if (!TeamBrawls.IsTeammatesBrawl(id))
                {
                    Landed++;
                    return;
                }

                __result = false;
                Discarded++;

                if (!_reported)
                {
                    _reported = true;
                    MelonLogger.Msg("[Enemy] a blow from an NPC in a teammate's brawl was discarded here. Their " +
                        "game reports it as a knockout, which this copy's own blow would not be.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] deciding whether a driven creature's blow lands failed: " + e);
            }
        }

        /// <summary>
        /// Client: whether a blow from this source is the host's to report, so it must not land here as well:
        /// an NPC of a brawl run elsewhere.
        /// Asked at the hit filter and at the damage funnel (DownedState), since area attacks skip the filter.
        /// </summary>
        internal static bool HostAnswers(Entity? source)
        {
            return IsDrivenStriker(source, out EnemyNetId id) && TeamBrawls.IsTeammatesBrawl(id);
        }

        private static bool IsDrivenStriker(Entity? source, out EnemyNetId id)
        {
            id = EnemyNetId.None;
            if (CoopSession.State != CoopSessionState.Joined || source == null) return false;

            // A boss strikes through a hit entity of its own (the Great Tusk), not its Actor.
            Actor? striker = source.TryCast<Actor>() ?? source.GetComponentInParent<Actor>();
            if (striker == null) return false;

            return EnemyRegistry.TryGetId(striker, out id) && EnemyRegistry.IsDriven(id);
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
