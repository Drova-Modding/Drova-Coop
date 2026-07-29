using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Puts the blows other players landed onto the local player, through the game's own damage path.
    ///
    /// This is the far half of <see cref="DamageWatch"/>: the attacker's machine calculated a swing
    /// against a body that could not be hurt there and said so, and this is where that becomes health
    /// coming off a character. Doing it here rather than where the message arrives is not tidiness - the
    /// game may only be touched from the frame loop, and a report routinely arrives before there is a
    /// player to apply it to.
    ///
    /// **It goes through <c>ChangeHealth</c> rather than writing the figure.** <c>SetHealth</c> stores
    /// <c>_curHealth</c> directly and skips the entire calculation, which sounds like the honest way to
    /// apply a number somebody else worked out and is in fact the dangerous one: damage blockers live
    /// inside that calculation, and the guard that turns a killing blow into a knockout - see
    /// <see cref="DownedState"/> - is one of them. A raw write reaching zero would latch <c>_isDead</c>,
    /// which nothing in the game ever clears, and the player would be permanently dead with the mod's own
    /// revive standing by unused.
    /// </summary>
    internal static class RemoteDamage
    {
        /// <summary>
        /// Applies everything that has arrived for the local player. Call once per frame from a loaded
        /// world.
        /// </summary>
        internal static void Update()
        {
            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player) || player == null) return;

            Health health = player.GetHealth();
            if (health == null) return;

            while (CoopSession.TryTakeDamage(out DamageReportMessage report))
            {
                Apply(health, report);
            }
        }

        /// <summary>
        /// Hands one blow to the local player's health.
        /// </summary>
        private static void Apply(Health health, in DamageReportMessage report)
        {
            try
            {
                if (!GameplaySettingsHandler.TryGet(out GameplaySettingsHandler settings)) return;

                // Physical, always, and that is a known limitation rather than an oversight. The damage
                // type decides which resistance answers it, and the type is a ScriptableObject on the
                // attacker's machine with no id this protocol can carry yet - so a spell crossing as
                // physical is resisted by armor instead of by wards. It is also the type essentially every
                // weapon deals, and abilities are not replicated at all yet, so today it is right far more
                // often than not.
                DamageStatDesc physical = settings.PhysicalDamageStatDesc;
                if (physical == null) return;

                // Built with no source on purpose, and this is the load-bearing detail of the whole class.
                // The constructor runs the source's own damage stats over the value - HealthChange
                // .AddStatMultiplikator - so naming the attacker's body here would add their weapon damage
                // to a figure that already includes it, and re-roll a crit that has already been rolled.
                //
                // Nothing needed is lost with it null. A melee blow is in the block angle regardless -
                // AA_BlockAction.IsInBlockAngle only measures one for a projectile - so the victim's guard,
                // their stamina and the perfect-block window all still decide the outcome. What the source
                // would have carried is attacker-side feedback: the aggro tag, the killed-by credit and the
                // block punishment, each of which already happened on the attacker's own machine against
                // their copy of this body.
                HealthChange change = new(null, new HealthChangeValue(report.Amount, physical));

                // The guard, not because anything replicates health today, but because the funnel this
                // write goes through is the one DamageWatch sits on. Without it the local player's own
                // body would look like a remote one to nothing - TryFindOwner only knows puppets - but the
                // moment health replication grows a second hook, this is the write that would echo.
                using (RemoteApply<HealthScope>.Enter())
                {
                    health.ChangeHealth(change);
                }
            }
            catch (Exception e)
            {
                // Swallowed, like the watch on the other side. A report that cannot be applied is a hit
                // that did not land; an exception let out of the frame loop is every hit after it too.
                MelonLogger.Error("[Dmg] applying a reported blow failed: " + e);
            }
        }
    }
}
