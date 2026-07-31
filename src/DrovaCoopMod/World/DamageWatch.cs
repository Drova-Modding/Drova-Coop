using Drova_Modding_API;
using Drova_Modding_API.Access;
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
    ///
    /// **What it reports is the potential change, not the actual one.** The two differ by everything this
    /// machine's copy of the victim knows about defending itself, which is their equipment and nothing
    /// else: no talents, no status effects, and a stamina pool the body was handed for free so it would
    /// stop spending one it does not own. Resistance and guard therefore belong on the far side, where the
    /// character is real, and what crosses is the blow rather than the wound. Everything that shaped the
    /// number before this point - the crit roll, the attacker's own stats - was computed against the real
    /// local player and is already in it.
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

            // The same funnel, one step earlier, for the one thing that must never be allowed to happen at
            // all rather than reported after it has. See KeepPuppetsAlive.
            Hooking.TryPrefix(harmony, typeof(Health), "CheckEvents",
                typeof(DamageWatch), nameof(CheckEventsPrefix));
        }

        /// <summary>
        /// Stops a remote player's body from ever reaching zero health.
        ///
        /// **A puppet must not die, and refusing the death after the fact is not the same thing.** The
        /// death flag latches inside this funnel and nothing in the game clears it, which the stand-up path
        /// already works around by writing the field directly. But the flag is only the switch: setting it
        /// raises <c>DiedEvent</c>, and every <c>ADeathBhvr</c> on the body acts on that once and for all -
        /// the animator is parked in its death state, the sprites are tinted, the health object is
        /// deactivated, the colliders are replaced and the hit receivers are switched off. Clearing
        /// <c>_isDead</c> afterwards leaves every one of those in place, so the body stands up as a grey
        /// corpse that will not move, which is what a player on the floor who never gets up looks like.
        ///
        /// So the value is lifted before the check reads it. One point of health is enough: the far side is
        /// where that player's real health lives, this figure is a copy that its owner's next packet
        /// overwrites, and a knockout - which is what put them here - has no health test of its own.
        ///
        /// **Not invincibility, which this body already has.** <c>SetHealth</c> ignores invincibility
        /// entirely, and it is what both the replicated figure and the game's own knockout go through.
        /// </summary>
        private static void CheckEventsPrefix(Health __instance)
        {
            try
            {
                if (__instance == null) return;
                if (__instance.CurHealth > 0) return;

                Actor? actor = __instance.OwnerEntity?.TryCast<Actor>();
                if (actor == null) return;

                if (!RemotePlayers.IsPuppet(actor)) return;

                __instance._curHealth = 1;

                _keptAlive++;

                if (_keptAlive == 1)
                {
                    MelonLogger.Msg("[Dmg] a remote player's body was about to die here and was held at one " +
                        "health instead. Their own game decides whether they are down; a corpse on this " +
                        "machine could never be stood back up.");
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Dmg] holding a remote player's body alive failed: " + e);
            }
        }

        /// <summary>How many times a puppet was held back from dying. For <c>coop_status</c>.</summary>
        internal static int KeptAlive => _keptAlive;

        private static int _keptAlive;

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
#if DEBUG
                // Before every guard, because what it is measuring is what reaches the funnel at all.
                Diagnostics.EnemyProbe.Count(__instance);
#endif

                // The write this mod itself just made, arriving back through the funnel it was always
                // going to arrive back through. Reporting it would tell the far player about their own
                // health as though this machine had discovered it.
                if (RemoteApply<HealthScope>.InProgress) return;

                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;
                if (changeArgs == null) return;

                HealthChange change = changeArgs.HealthChange;
                if (change == null) return;

                // Before the damage guard, not after. A creature can be killed by a write that carries no
                // damage at all - SetHealth builds its change with the source-only constructor, leaving
                // PotentialChange at zero - so asking about death below the next line would miss every
                // scripted kill.
                EnemyDeathWatch.Notice(__instance, changeArgs);

                // Healing is left alone for now. It is a real case - a bandage thrown to a friend - but it
                // arrives through the same funnel as the passive regeneration every actor runs constantly,
                // and telling those apart is its own piece of work rather than a sign flip.
                int potential = change.PotentialChange;
                if (potential >= 0) return;

                bool struckByThePlayerHere = IsFromLocalPlayer(change.Source);

                if (!RemotePlayers.TryFindOwner(__instance, out PlayerId victim))
                {
                    // A creature, so only what the person here struck is news. It is hit by this machine's
                    // own copy of everything else fighting it, and that copy is running on the other
                    // machine too.
                    if (!struckByThePlayerHere) return;

                    // It may be a creature the host owns - in which case the number this machine just
                    // computed was thrown away by write-protection and only means something once the host
                    // has answered it.
                    ReportToOwner(__instance, change, -potential);
                    return;
                }

                // Read off the same change, because both are things this machine worked out about a body
                // that cannot really feel them. Poise decides whether the blow staggers, and the bleed flag
                // is the permission to open a wound - the wound's size is left to the victim, who is the
                // only one who knows what actually landed.
                if (struckByThePlayerHere)
                {
                    CoopSession.ReportDamage(victim, -potential, change.PotentialPoiseDmg, change._appliesBleed,
                        TypeOf(change), DamageReportMessage.OriginPlayer);

                    return;
                }

                // **A creature this machine drives, hitting somebody who is not here.** The far player's
                // copy of this creature has its AI switched off and is moved by the wire, so it never
                // swings there: whatever it does to them has to be done from here or not at all. That is
                // the same division as for a player's blow, and it was missing - a claimed creature could
                // chase a teammate across a field, connect on this screen, and cost them nothing.
                //
                // Claimed rather than merely nearby, because that is what makes this machine the one that
                // decides. An unclaimed creature is running on both machines independently, and the far
                // copy is already hitting its own player with its own numbers.
                if (!IsACreatureThisMachineDrives(change.Source)) return;

                CoopSession.ReportDamage(victim, -potential, change.PotentialPoiseDmg, change._appliesBleed,
                    TypeOf(change), DamageReportMessage.OriginCreature);
            }
            catch (Exception e)
            {
                // Swallowed rather than allowed out. This runs inside the game's own damage path, and an
                // exception escaping it would take the hit with it - a coop bug that reads as enemies
                // whose attacks sometimes do nothing.
                MelonLogger.Error("[Dmg] watching a health change failed: " + e);
            }
        }

        /// <summary>
        /// Tells the host about a blow this player landed on a creature the host owns.
        ///
        /// **Only a client does this, and only for creatures the host has claimed.** A host landing its
        /// own blow needs no message - it is already the one deciding - and an unclaimed creature is one
        /// both machines are still simulating separately, where a report would be damage applied twice.
        ///
        /// What crosses is the potential change rather than what landed. The client's copy of the creature
        /// has the right authored stats but its own independently-running behavior, so its blockers and
        /// its guard need not match the host's; sending the wound would have the host mitigate a figure
        /// this machine had already mitigated.
        /// </summary>
        private static void ReportToOwner(Health health, HealthChange change, int amount)
        {
            if (CoopSession.State != CoopSessionState.Joined) return;

            Actor? actor = health.OwnerEntity?.TryCast<Actor>();
            if (actor == null) return;

            if (!EnemyRegistry.TryGetId(actor, out EnemyNetId id)) return;

            CoopSession.ReportEnemyDamage(id, amount, change.PotentialPoiseDmg, change._appliesBleed, TypeOf(change));
        }

        /// <summary>
        /// Whether the person playing at this machine is what struck the blow.
        ///
        /// The source is an <c>Entity</c> rather than an actor, and it is null for a good deal of what
        /// damages people - falling, drowning, a status effect nobody owns. All of those are things the
        /// far player's own game is running too, so a null source is not this machine's news.
        ///
        /// Compared by pointer, since two proxies over one native actor are different managed objects.
        /// </summary>
        private static bool IsFromLocalPlayer(Entity? source)
        {
            if (source == null) return false;
            if (!PlayerAccess.TryGetPlayer(out Actor localPlayer) || localPlayer == null) return false;

            return source.Pointer == localPlayer.Pointer;
        }

        /// <summary>
        /// Whether this blow came from a creature this machine has claimed the driving of.
        ///
        /// **Claimed, which is a stronger thing than present.** A handle is never given back to the pool,
        /// so a creature the host has released still has one; speaking for that creature would be speaking
        /// for something both machines are simulating separately again, and the far side would take the
        /// blow twice. <see cref="EnemyRegistry.TryGetSessionId"/> is the check that draws the line, and on
        /// a client it answers about bound creatures - which is why the state is asked first. A client
        /// reporting its copy's swings would be the far machine's own creature hitting it by proxy.
        /// </summary>
        private static bool IsACreatureThisMachineDrives(Entity? source)
        {
            if (source == null) return false;
            if (CoopSession.State != CoopSessionState.Hosting) return false;

            Actor? actor = source.TryCast<Actor>();
            if (actor == null) return false;

            return EnemyRegistry.TryGetSessionId(actor, out _);
        }

        /// <summary>
        /// Which of the three damage types this blow should be answered by on the far side.
        ///
        /// A blow can carry several values of different types, and only one byte crosses. Magical wins
        /// when any part of it is magical, because being resisted by wards when part of the blow was
        /// physical is a smaller error than a spell being stopped by armor - which is what happened when
        /// every blow crossed as physical.
        /// </summary>
        private static byte TypeOf(HealthChange change)
        {
            Il2CppSystem.Collections.Generic.List<HealthChangeValue> values = change._healthChangeValues;
            if (values == null) return DamageReportMessage.TypePhysical;

            if (!DamageAccess.TryGetMagicalDamageType(out DamageStatDesc magical)) return DamageReportMessage.TypePhysical;

            DamageAccess.TryGetPureDamageType(out DamageStatDesc pure);

            bool anyPure = false;

            for (int index = 0; index < values.Count; index++)
            {
                DamageStatDesc type = values[index].DmgType;
                if (type == null) continue;

                // By pointer, for the usual reason: two proxies over one native object are different
                // managed instances.
                if (type.Pointer == magical.Pointer) return DamageReportMessage.TypeMagical;
                if (pure != null && type.Pointer == pure.Pointer) anyPure = true;
            }

            return anyPure ? DamageReportMessage.TypePure : DamageReportMessage.TypePhysical;
        }
    }
}
