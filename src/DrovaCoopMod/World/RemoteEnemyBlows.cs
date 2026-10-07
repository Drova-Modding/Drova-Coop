using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.HitSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Stops a host-driven creature's replayed swing from also hitting this client's player, since the host reports that blow.
    /// Refused at the hit filter so stagger, knockback and poison are refused too.
    /// </summary>
    internal static class RemoteEnemyBlows
    {
        private static bool _reported;

        /// <summary>Blows from host-driven creatures discarded here. For <c>coop_status</c>.</summary>
        internal static int Discarded { get; private set; }

        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // Hit entry and hit update both pass through here with the built arguments in hand.
            Hooking.TryPostfix(harmony, typeof(HitReceiveHandler), "CheckFilterWithBhvrs",
                typeof(RemoteEnemyBlows), nameof(CheckFilterWithBhvrsPostfix));
        }

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            Discarded = 0;
        }

        /// <summary>Turns an accepted hit into a refused one when the host has already answered for it.</summary>
        private static void CheckFilterWithBhvrsPostfix(HitReceiveHandler __instance, HitTriggerArgs args,
            ref bool __result)
        {
            try
            {
                if (!__result) return;

                if (CoopSession.State != CoopSessionState.Joined) return;

                if (args == null) return;

                Entity attacker = args.Entity;
                if (attacker == null) return;

                Actor? striker = attacker.TryCast<Actor>();
                if (striker == null) return;

                if (!EnemyRegistry.TryGetId(striker, out EnemyNetId id)) return;
                if (!EnemyRegistry.IsDriven(id)) return;

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

        /// <summary>Whether this receiver belongs to the local player, who carries several receivers.</summary>
        private static bool IsTheLocalPlayer(HitReceiveHandler receiver)
        {
            return RemotePlayers.IsLocalPlayer(receiver.GetComponentInParent<Actor>());
        }
    }
}
