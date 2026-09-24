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
            if (!CoopSession.IsLive) return;
            if (!PlayerAccess.TryGetPlayer(out Actor player)) return;

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
                if (!RemoteEnemyHealth.TryResolveType(report.DamageType, out DamageStatDesc type)) return;

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
                // loss of stance using numbers only this machine has. See RemoteEnemyHealth.ApplyPoise.
                if (report.PoiseDamage > 0f)
                {
                    change.SetPotentialPoiseDmg(report.PoiseDamage);
                    change.AfterHealthChangedEvent.AddEventListener(new Action<HealthChangeArgs>(args => RemoteEnemyHealth.ApplyPoise(player, args, "[Dmg]")));
                }

                // What the blow worked out to, collected so the floor below can ask. Resolved stays
                // false when ChangeHealth refuses the change outright, which it does for a player in
                // dialogue or mid-knockout.
                int landed = 0;
                int blocked = 0;
                bool resolved = false;
                change.AfterHealthChangedEvent.AddEventListener(new Action<HealthChangeArgs>(args =>
                {
                    if (args == null) return;
                    landed = args.ActualChange;
                    blocked = args.BlockedValue;
                    resolved = true;
                }));

                // The guard, not because anything replicates health today, but because the funnel this
                // write goes through is the one DamageWatch sits on. Without it the local player's own
                // body would look like a remote one to nothing - TryFindOwner only knows puppets - but the
                // moment health replication grows a second hook, this is the write that would echo.
                using (RemoteApply<HealthScope>.Enter())
                {
                    health.ChangeHealth(change);

                    // Another player's blow always costs at least one point of health. The game's
                    // armor math truncates toward zero with no minimum (measured, not read from the
                    // decompile), so a light weapon against high resistance lands nothing at all.
                    //
                    // Only a player's blow: a weak creature landing nothing is vanilla behavior. Only
                    // when nothing was blocked, so a guarded blow stays guarded. Only when the change
                    // resolved, so a refused blow stays refused.
                    if (report.Origin == DamageReportMessage.OriginPlayer
                        && report.Amount > 0 && resolved && landed == 0 && blocked == 0)
                    {
                        HealthChange floorBlow = new(null, new HealthChangeValue(1, type));
                        floorBlow.SetIgnoreArmor();
                        health.ChangeHealth(floorBlow);
                    }
                }

                // Only a player's blow is answered. A creature's is driven by whoever owns the creature,
                // and its copy on that machine is not a puppet whose guard needs correcting.
                if (report.Origin == DamageReportMessage.OriginPlayer && resolved)
                {
                    BlockVerdicts.Answer(report.Attacker, report.Sequence, player, landed, blocked);
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
        /// Takes a teammate's verdict about a blow this player struck, and plays it out on their body.
        ///
        /// Late by a round trip, which is why only a guard break is acted on: the body has been holding
        /// in the meantime, because its own decision was refused, and a break is the one correction that
        /// can be added to a body that has not had it rather than taken back from one that has.
        /// </summary>
        internal static void TakeVerdict(in BlockVerdictMessage message)
        {
            try
            {
                if (!RemotePlayers.Bodies.TryGetValue(message.Victim, out RemotePlayer? body)) return;
                if (body == null || body.Body == null) return;

                BlockVerdicts.Apply(message.Victim, message.Sequence, body.Body, message.Verdict);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Dmg] taking a teammate's block verdict failed: " + e);
            }
        }
    }
}
