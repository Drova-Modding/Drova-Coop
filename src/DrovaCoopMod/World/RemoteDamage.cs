using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.Poise;
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
    /// It goes through <c>DamageAccess</c> rather than writing the figure, which runs the game's own
    /// health calculation. Writing <c>_curHealth</c> would skip that calculation, and the damage blockers
    /// live inside it - including the guard that turns a killing blow into a knockout, see
    /// <see cref="DownedState"/>. A raw write reaching zero would latch <c>_isDead</c>, which nothing in
    /// the game ever clears, and the player would be permanently dead with the mod's own revive standing
    /// by unused. That recipe is the API's rather than this mod's because every mod that deals damage
    /// needs it and would get it wrong the same way.
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
                Apply(player, health, report);
            }
        }

        /// <summary>
        /// Hands one blow to the local player's health.
        /// </summary>
        private static void Apply(Actor player, Health health, in DamageReportMessage report)
        {
            try
            {
                if (!TryResolveType(report.DamageType, out DamageStatDesc type)) return;

                // Built here rather than handed to the API's one-liner, because a reported blow carries
                // more than a figure. The API deals damage; this has to deal the same blow.
                //
                // No source, and that is the load-bearing detail. Naming a source runs its own damage
                // stats over the value, so naming the attacker's body would add their weapon damage to a
                // figure that already includes it. Nothing needed is lost: the victim's guard, stamina and
                // perfect-block window still decide the outcome, and what a source would have carried -
                // the aggro tag, the killed-by credit, the block punishment - already happened on the
                // attacker's own machine against their copy of this body.
                HealthChange change = new(null, new HealthChangeValue(report.Amount, type));

                // The permission to open a wound, not the wound itself. The game works the size out from
                // the damage that actually lands, inside the health change below, so the victim's own
                // armor and resistances shape the bleeding rather than the attacker's guess at them.
                if (report.AppliesBleed != 0)
                {
                    change.SetAppliesBleed(true);
                }

                // The raw figure, handed to the change rather than to the stance, and collected again once
                // the change has been resolved. What arrives is what the blow was worth before this player
                // had any say in it - the same thing the damage figure is - and the game turns that into a
                // loss of stance using numbers only this machine has. See ApplyPoise.
                if (report.PoiseDamage > 0f)
                {
                    change.SetPotentialPoiseDmg(report.PoiseDamage);
                    change.AfterHealthChangedEvent.AddEventListener(new Action<HealthChangeArgs>(args => ApplyPoise(player, args)));
                }

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

        /// <summary>
        /// Finds the damage type a reported blow should be answered by.
        ///
        /// Three types cross, not every type. Damage types are authored assets and the game keeps no
        /// database to resolve one from an id, so only the ones it names itself can be found here. That
        /// covers the distinction that actually matters - a spell answered by wards rather than by armor -
        /// and leaves exotic authored types arriving as physical, which is what every blow used to be.
        /// </summary>
        private static bool TryResolveType(byte damageType, out DamageStatDesc type)
        {
            switch (damageType)
            {
                case DamageReportMessage.TypeMagical:
                    if (DamageAccess.TryGetMagicalDamageType(out type)) return true;
                    break;

                case DamageReportMessage.TypePure:
                    if (DamageAccess.TryGetPureDamageType(out type)) return true;
                    break;
            }

            // Physical is both the default and the fallback: a build whose settings have no magical type
            // yet should still take the blow rather than ignore it.
            return DamageAccess.TryGetPhysicalDamageType(out type);
        }

        /// <summary>
        /// The share of a blow's poise that a player takes when the blow was theirs to answer.
        ///
        /// The game reads this factor off the <em>attacker</em> despite the parameter it lands in being
        /// named for the target, and it is one for anything it recognizes as a player. Every blow that
        /// reaches here was struck by a real player on their own machine, so it is always one - the value
        /// is named rather than inlined because the reason it is one is not obvious from the number.
        /// </summary>
        private const float PlayerPoiseDmgFactor = 1f;

        /// <summary>
        /// Takes the stance the blow actually cost off the local player, once the health change that
        /// carried it has been resolved.
        ///
        /// The figure on the wire is what the blow was worth, not what it takes. Draining that
        /// directly was wrong in the way applying raw damage directly would be: it staggered a player who
        /// had just perfectly blocked, because nothing asked whether the blow landed. The game's own
        /// answer is <c>DamageCalculator.CalcPoiseDamage</c>, and every input it needs beyond the raw
        /// figure - what actually landed, what the guard absorbed, how many times it hit - belongs to
        /// this machine and is only known once <c>ChangeHealth</c> has run. A fully blocked blow works
        /// out to a factor of zero there, and a partly blocked one scales, both without this having to
        /// know how blocking works.
        ///
        /// This is the step <c>ABI_Bhvr_HitEffect_Damage.HandlePoise</c> performs after an ordinary hit.
        /// It is missing here for the same reason the damage was: the behavior that would have done it
        /// lives on the attacker's machine, so the blow arrives as a number with nobody to finish it.
        /// </summary>
        private static void ApplyPoise(Actor player, HealthChangeArgs args)
        {
            try
            {
                if (args == null || player == null) return;

                HealthChange change = args.HealthChange;
                if (change == null) return;

                float raw = change.PotentialPoiseDmg;
                if (raw <= 0f) return;

                PoiseController poise = player.PoiseController;
                if (poise == null) return;

                float loss = DamageCalculator.CalcPoiseDamage(
                    raw, args.ActualChange, args.BlockedValue, change.HitCount, PlayerPoiseDmgFactor);

                if (loss <= 0f) return;

                poise.ChangePoise(-loss);
            }
            catch (Exception e)
            {
                // Swallowed here as well as around the caller, because this runs inside the game's own
                // event dispatch rather than in the frame loop: an exception let out would abandon the
                // rest of what the health change was going to do.
                MelonLogger.Error("[Dmg] applying the stance a reported blow cost failed: " + e);
            }
        }
    }
}
