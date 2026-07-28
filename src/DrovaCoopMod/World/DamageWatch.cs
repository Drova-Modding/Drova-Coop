using Drova_Modding_API;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Watches every health change this machine makes, and picks out the ones that happened to somebody
    /// else's body.
    ///
    /// <c>Health.CheckEvents</c> is the funnel, and it is the private one on purpose. <c>ChangeHealth</c>
    /// and <c>SetHealth</c> are independent write paths - the second stores <c>_curHealth</c> itself and
    /// never calls the first - and both are inlined at some of their call sites, so a patch on either is
    /// accepted and then does not run for a share of the changes that actually happen. With all three
    /// patched at once this one recorded 77 changes against <c>ChangeHealth</c>'s 58, which is to say
    /// roughly a quarter of them reached the game through a copy whose own patch never fired.
    ///
    /// **It observes and cannot decide.** By the time it runs, <c>_curHealth</c> has already been written
    /// and clamped; there is nothing here to veto. That is enough, because a remote body is invincible -
    /// see <see cref="PuppetFactory"/> - so the number reaching this point was computed and then thrown
    /// away, and reporting it to the player it belongs to is the only thing that makes it real.
    /// </summary>
    internal static class DamageWatch
    {
        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            // The ref parameter is not the obstacle it looks like. HealthChangeArgs is a class, so this is
            // a reference to a proxy rather than a struct being marshaled by address, and the interop
            // exposes it as `void CheckEvents(ref HealthChangeArgs changeArgs)` with that parameter name -
            // which is what the postfix below has to match for Harmony to bind it.
            Hooking.TryPostfix(harmony, typeof(Health), "CheckEvents",
                typeof(DamageWatch), nameof(CheckEventsPostfix));
        }

        /// <summary>
        /// Reports damage dealt to a remote player's body.
        ///
        /// Runs for every actor in the scene, so the first thing it does is establish that this one is not
        /// its business. Everything below the guards costs nothing on the overwhelming majority of calls,
        /// which matters because this sits on the path of every point of damage in the game.
        /// </summary>
        private static void CheckEventsPostfix(Health __instance, ref HealthChangeArgs changeArgs)
        {
            try
            {
                // The write this mod itself just made, arriving back through the funnel it was always
                // going to arrive back through. Reporting it would tell the far player about their own
                // health as though this machine had discovered it.
                if (RemoteApply<HealthScope>.InProgress) return;

                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (changeArgs == null) return;

                // Healing is left alone for now. It is a real case - a bandage thrown to a friend - but it
                // arrives through the same funnel as the passive regeneration every actor runs constantly,
                // and telling those apart is its own piece of work rather than a sign flip.
                int change = changeArgs.ActualChange;
                if (change >= 0) return;

                if (!RemotePlayers.TryFindOwner(__instance, out PlayerId victim)) return;

                MelonLogger.Msg($"[Dmg] {victim} would take {-change} " +
                    $"(pre {changeArgs.PreChangeHealth}, blocked {changeArgs.BlockedValue}).");
            }
            catch (Exception e)
            {
                // Swallowed rather than allowed out. This runs inside the game's own damage path, and an
                // exception escaping it would take the hit with it - a coop bug that reads as enemies
                // whose attacks sometimes do nothing.
                MelonLogger.Error("[Dmg] watching a health change failed: " + e);
            }
        }
    }
}
