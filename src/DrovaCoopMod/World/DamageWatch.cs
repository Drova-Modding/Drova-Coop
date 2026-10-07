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
    /// Reports blows this machine computed against other players' bodies and host-owned creatures.
    /// Hooks the private <c>Health.CheckEvents</c> funnel, since <c>ChangeHealth</c> and <c>SetHealth</c> are
    /// separate paths and inlined at some call sites. Reports the unmitigated blow, so the victim's machine applies defense.
    /// </summary>
    internal static class DamageWatch
    {
        /// <summary>Applies the patch. Called once at melon init.</summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(Health), "CheckEvents",
                typeof(DamageWatch), nameof(CheckEventsPostfix));

            Hooking.TryPrefix(harmony, typeof(Health), "CheckEvents",
                typeof(DamageWatch), nameof(CheckEventsPrefix));
        }

        // Holds a puppet at one health before the death check: DiedEvent's ADeathBhvr effects cannot be undone,
        // and SetHealth ignores the puppet's invincibility.
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

                KeptAlive++;

                if (KeptAlive == 1)
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
        internal static int KeptAlive { get; private set; }

        /// <summary>Blows the player here landed on a teammate's copy and reported to that teammate.</summary>
        internal static int ReportedPlayerBlows { get; private set; }

        /// <summary>
        /// Blows a teammate's copy landed on the player here by itself. Should stay zero, since their blows
        /// arrive as reports.
        /// </summary>
        internal static int CopyBlowsOnLocal { get; private set; }

        private static void CheckEventsPostfix(Health __instance, ref HealthChangeArgs changeArgs)
        {
            try
            {
                // This mod's own write, echoing back.
                if (RemoteApply<HealthScope>.InProgress) return;

                if (!CoopSession.IsLive) return;
                if (changeArgs == null) return;

                HealthChange change = changeArgs.HealthChange;
                if (change == null) return;

                // Before the damage guard: SetHealth's scripted kills carry a zero PotentialChange.
                EnemyDeathWatch.Notice(__instance, changeArgs);

                // Healing is not reported: it shares this funnel with passive regeneration.
                int potential = change.PotentialChange;
                if (potential >= 0) return;

                bool struckByThePlayerHere = RemotePlayers.IsLocalPlayer(change.Source);

                if (change.Source != null && RemotePlayers.IsPuppet(change.Source)
                    && PlayerAccess.TryGetPlayer(out Actor self) && self.GetHealth()?.Pointer == __instance.Pointer)
                {
                    CopyBlowsOnLocal++;
                }

                if (!RemotePlayers.TryFindOwner(__instance, out PlayerId victim))
                {
                    // A creature: only what the player here struck is news.
                    if (!struckByThePlayerHere) return;

                    if (CoopSession.State == CoopSessionState.Hosting)
                    {
                        RelayIntoLease(__instance, change, -potential);
                        return;
                    }

                    ReportToOwner(__instance, change, -potential);
                    return;
                }

                if (struckByThePlayerHere)
                {
                    ReportedPlayerBlows++;
                    bool bleeds = Bleeds(change);
                    foreach ((byte type, int part, bool first) in Split(change, -potential))
                    {
                        CoopSession.ReportDamage(victim, part, first ? change.PotentialPoiseDmg : 0f, first && bleeds,
                            type, DamageReportMessage.OriginPlayer);
                    }

                    return;
                }

                // A creature this machine drives hit a puppet. Its AI-less far copy never swings, so report it.
                if (!IsACreatureThisMachineDrives(change.Source)) return;

                foreach ((byte type, int part, bool first) in Split(change, -potential))
                {
                    CoopSession.ReportDamage(victim, part, first ? change.PotentialPoiseDmg : 0f, first && Bleeds(change),
                        type, DamageReportMessage.OriginCreature);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Dmg] watching a health change failed: " + e);
            }
        }

        // Sends the host player's blow to the teammate whose scripted brawl the target is leased to.
        private static void RelayIntoLease(Health health, HealthChange change, int amount)
        {
            Actor? actor = health.OwnerEntity?.TryCast<Actor>();
            if (actor == null) return;

            if (!EnemyRegistry.TryGetSessionId(actor, out EnemyNetId id)) return;
            if (!EnemyEngagements.TryGetLease(id, out PlayerId player)) return;

            foreach ((byte type, int part, bool first) in Split(change, amount))
            {
                CoopSession.SendLeasedEnemyDamage(player, id, part, first ? change.PotentialPoiseDmg : 0f,
                    first && Bleeds(change), type);
            }
        }

        // Client only: tells the host about a blow on a creature it owns.
        private static void ReportToOwner(Health health, HealthChange change, int amount)
        {
            if (CoopSession.State != CoopSessionState.Joined) return;

            Actor? actor = health.OwnerEntity?.TryCast<Actor>();
            if (actor == null) return;

            if (!EnemyRegistry.TryGetId(actor, out EnemyNetId id)) return;

            // A leased brawl is fought here for real, the blow already landed.
            if (EngageWatch.IsBrawling(id)) return;

            // The host has no brawl, so it must be told this blow may not kill.
            byte flags = 0;
            if (PlayerAccess.TryGetPlayer(out Actor player)
                && player.CustomStats != null && player.CustomStats.IsInBrawl)
            {
                flags = EnemyDamageMessage.FlagBrawl;
            }

            foreach ((byte type, int part, bool first) in Split(change, amount))
            {
                CoopSession.ReportEnemyDamage(id, part, first ? change.PotentialPoiseDmg : 0f, first && Bleeds(change),
                    type, flags);
            }
        }

        /// <summary>
        /// Whether this blow came from a creature the host drives. An undriven creature runs at both ends, so
        /// the victim's machine already takes its blows.
        /// </summary>
        internal static bool IsACreatureThisMachineDrives(Entity? source)
        {
            if (source == null) return false;

            Actor? actor = source.TryCast<Actor>();
            if (actor == null) return false;

            if (!EnemyRegistry.TryGetSessionId(actor, out EnemyNetId id)) return false;

            if (CoopSession.State != CoopSessionState.Hosting) return false;

            // A copy mirroring a client's leased fight: the blow already landed there.
            if (EnemyEngagements.TryGetLease(id, out _)) return false;

            return EnemyRegistry.DrivesOnHost(id);
        }

        // A crit bleeds too, the game's own rule in HealthChange.AfterHealthChanged.
        private static bool Bleeds(HealthChange change) => change._appliesBleed || (change._wasCrit && change._allowCrit);

        // One part per damage type, sized by share. Poise and bleed ride on the largest part only.
        private static List<(byte Type, int Amount, bool First)> Split(HealthChange change, int total)
        {
            List<(byte Type, int Amount, bool First)> parts = new();
            if (total <= 0) return parts;

            Il2CppSystem.Collections.Generic.List<HealthChangeValue> values = change._healthChangeValues;
            DamageAccess.TryGetMagicalDamageType(out DamageStatDesc magical);
            DamageAccess.TryGetPureDamageType(out DamageStatDesc pure);

            int[] raw = new int[3];
            if (values != null)
            {
                for (int index = 0; index < values.Count; index++)
                {
                    HealthChangeValue value = values[index];
                    if (!value.IsDamaging || value.Amount == 0) continue;

                    DamageStatDesc type = value.DmgType;
                    int slot = type != null && magical != null && type.Pointer == magical.Pointer ? DamageReportMessage.TypeMagical
                        : type != null && pure != null && type.Pointer == pure.Pointer ? DamageReportMessage.TypePure
                        : DamageReportMessage.TypePhysical;
                    raw[slot] += Math.Abs(value.Amount);
                }
            }


            int sum = raw[0] + raw[1] + raw[2];
            if (sum == 0)
            {
                parts.Add((DamageReportMessage.TypePhysical, total, true));
                return parts;
            }

            // Rounding remainder goes to the largest part.
            int largest = 0;
            for (int slot = 1; slot < 3; slot++) if (raw[slot] > raw[largest]) largest = slot;

            int given = 0;
            int[] share = new int[3];
            for (int slot = 0; slot < 3; slot++)
            {
                share[slot] = (int)((long)total * raw[slot] / sum);
                given += share[slot];
            }

            share[largest] += total - given;

            parts.Add(((byte)largest, share[largest], true));
            for (int slot = 0; slot < 3; slot++)
            {
                if (slot != largest && share[slot] > 0) parts.Add(((byte)slot, share[slot], false));
            }

            return parts;
        }
    }
}
