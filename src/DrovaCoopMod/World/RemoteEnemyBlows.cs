using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.HitSystem;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Stops a creature the host drives from landing its own blows on this client's player.
    ///
    /// **One creature, two machines, and only one of them gets to decide.** The host owns a claimed
    /// creature's damage: it computes the blow against the puppet standing in for this player and reports
    /// it, which is what <see cref="RemoteEnemyHealth"/> and <see cref="DamageWatch"/> arrange. The copy
    /// here is driven - its AI is off and its position, facing and swings arrive over the wire - so the
    /// swing it plays is a replay of a decision already made elsewhere. If that replay also connects, the
    /// player pays for the same attack twice: once because the host said so, and once because their own
    /// machine independently agreed.
    ///
    /// **Discarded at the filter rather than at the health**, because a hit is more than a number. Refusing
    /// only the damage would leave the stagger, the knockback and the poison the ability applies, each of
    /// which is also coming from the host on its own channel. Answering false here is the same outcome the
    /// game produces for a swing that fails an alignment check: nothing about the hit happens at all.
    ///
    /// **Only the person playing here, and only for creatures this machine has been given.** A driven
    /// creature hitting a local NPC is left alone: nothing about NPC health is synchronized, both machines
    /// run that fight independently, and cutting it short on one of them is a divergence rather than a fix.
    /// An unclaimed creature is left alone for the same reason it is on the sending side - it is really
    /// running here, and its blows are its own.
    /// </summary>
    internal static class RemoteEnemyBlows
    {
        private static int _discarded;
        private static bool _reported;

        /// <summary>
        /// How many blows from a host-driven creature were discarded here because the host is the one who
        /// reports them. For <c>coop_status</c>.
        ///
        /// **Zero while a client is being attacked is the number to distrust.** It means either that the
        /// driven copies never connect - which is what they appeared to do, and the reason the host had to
        /// start reporting their blows at all - or that the patch below applies and never runs. The two
        /// look identical from outside, and only this count tells them apart.
        /// </summary>
        internal static int Discarded => _discarded;

        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The single point both hit entry and hit update pass through, and the only one that has the
            // built arguments in hand. Reading the attacker from the trigger instead would mean asking it
            // for those arguments, which takes one out of a pool the caller is the one releasing.
            Hooking.TryPostfix(harmony, typeof(HitReceiveHandler), "CheckFilterWithBhvrs",
                typeof(RemoteEnemyBlows), nameof(CheckFilterWithBhvrsPostfix));
        }

        /// <summary>Forgets the count. Called when a session ends.</summary>
        internal static void Reset()
        {
            _discarded = 0;
        }

        /// <summary>
        /// Turns an accepted hit into a refused one when the host has already answered for it.
        ///
        /// A postfix that can only refuse. Everything the game had its own reasons to discard stays
        /// discarded, and the guards are ordered so the ordinary hit - the player striking something -
        /// costs a session-state comparison and nothing else.
        /// </summary>
        private static void CheckFilterWithBhvrsPostfix(HitReceiveHandler __instance, HitTriggerArgs args,
            ref bool __result)
        {
            try
            {
                if (!__result) return;

                // Only a client has creatures somebody else decides for.
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
                _discarded++;

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
                // Swallowed: this runs inside the hit system, and an exception let out would take the hit
                // with it - for everything in the world, not only for a driven creature.
                MelonLogger.Error("[Enemy] deciding whether a driven creature's blow lands failed: " + e);
            }
        }

        /// <summary>
        /// Whether this receiver belongs to the person playing at this machine.
        ///
        /// Walked up from the receiver rather than compared against a cached collider, because a player
        /// carries several of these - the body, the perception trigger, the ones abilities add - and which
        /// of them a given swing lands on is not knowable from here.
        ///
        /// Asked last, after the attacker has already been established as a driven creature, so the walk
        /// happens for the handful of hits that could be affected rather than for every hit in the game.
        /// </summary>
        private static bool IsTheLocalPlayer(HitReceiveHandler receiver)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return false;

            Actor? owner = receiver.GetComponentInParent<Actor>();

            // By pointer, since two proxies over one native actor are different managed objects.
            return owner != null && owner.Pointer == player.Pointer;
        }
    }
}
