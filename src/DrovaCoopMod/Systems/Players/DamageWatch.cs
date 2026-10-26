using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Players.Patches;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Reports blows this machine computed against other players' bodies and host-owned creatures.
    /// Hooks the private <c>Health.CheckEvents</c> funnel, since <c>ChangeHealth</c> and <c>SetHealth</c> are
    /// separate paths and inlined at some call sites. Reports the unmitigated blow, so the victim's machine applies defense.
    /// </summary>
    internal sealed class DamageWatch : ICoopSystem
    {
        /// <summary>How many times a puppet was held back from dying. For <c>coop_status</c>.</summary>
        internal static int KeptAlive { get; private set; }

        /// <summary>Blows the player here landed on a teammate's copy and reported to that teammate.</summary>
        internal static int ReportedPlayerBlows { get; private set; }

        /// <summary>
        /// Blows a teammate's copy landed on the player here by itself. Should stay zero, since their blows
        /// arrive as reports.
        /// </summary>
        internal static int CopyBlowsOnLocal { get; private set; }

        /// <summary>Client: blows between two creatures it runs, reported to the host.</summary>
        internal static int CreatureBlowsSent { get; private set; }

        /// <summary>Host: see <see cref="NoticeHostCopyFight"/>. Should stay zero.</summary>
        internal static int HostCopyFights { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(Health), "CheckEvents",
                typeof(DamageWatchPatches), nameof(DamageWatchPatches.CheckEventsPostfix));

            Hooking.TryPrefix(harmony, typeof(Health), "CheckEvents",
                typeof(DamageWatchPatches), nameof(DamageWatchPatches.CheckEventsPrefix));
        }

        // Holds a puppet at one health before the death check: DiedEvent's ADeathBhvr effects cannot be undone,
        // and SetHealth ignores the puppet's invincibility.
        internal static void CheckEventsPrefix(Health __instance)
        {
            try
            {
                if (__instance == null) return;
                if (__instance.CurHealth > 0) return;

                Entity? owner = __instance.OwnerEntity;
                Actor? actor = owner == null ? null : owner.TryCast<Actor>();
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

        internal static void CheckEventsPostfix(Health __instance, ref HealthChangeArgs changeArgs)
        {
            try
            {
                // This mod's own write, echoing back.
                if (RemoteApply<HealthScope>.InProgress) return;

                if (!CoopSession.IsLive) return;
                if (changeArgs == null) return;

                HealthChange? change = changeArgs.HealthChange;
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
                    // A creature: news when the player here struck it, or when a creature this client runs did, since the
                    // host's copy of a far fight stands still and its health refresh would heal the wound.
                    if (struckByThePlayerHere)
                    {
                        if (CoopSession.State == CoopSessionState.Hosting)
                        {
                            RelayIntoLease(__instance, change, -potential);
                            return;
                        }

                        ReportToOwner(__instance, change, -potential, fromCreature: false);
                        return;
                    }

                    if (CoopSession.State == CoopSessionState.Joined && IsACreatureTheClientRuns(change.Source))
                    {
                        ReportToOwner(__instance, change, -potential, fromCreature: true);
                    }
                    else if (CoopSession.State == CoopSessionState.Hosting)
                    {
                        NoticeHostCopyFight(__instance, change.Source);
                    }

                    return;
                }

                if (struckByThePlayerHere)
                {
                    ReportedPlayerBlows++;
                    bool bleeds = Bleeds(change);
                    foreach ((byte type, int part, bool first) in Split(change, -potential))
                    {
                        RemoteDamage.ReportDamage(victim, part, first ? change.PotentialPoiseDmg : 0f, first && bleeds,
                            type, DamageReportMessage.OriginPlayer);
                    }

                    return;
                }

                // A creature this machine drives hit a puppet. Its AI-less far copy never swings, so report it.
                if (!IsACreatureThisMachineDrives(change.Source)) return;

                foreach ((byte type, int part, bool first) in Split(change, -potential))
                {
                    RemoteDamage.ReportDamage(victim, part, first ? change.PotentialPoiseDmg : 0f, first && Bleeds(change),
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
            Entity? owner = health.OwnerEntity;
            Actor? actor = owner == null ? null : owner.TryCast<Actor>();
            if (actor == null) return;

            if (!EnemyRegistry.TryGetSessionId(actor, out EnemyNetId id)) return;
            if (!EnemyEngagements.TryGetLease(id, out PlayerId player)) return;

            foreach ((byte type, int part, bool first) in Split(change, amount))
            {
                RemoteEnemyHealth.SendLeasedEnemyDamage(player, id, part, first ? change.PotentialPoiseDmg : 0f,
                    first && Bleeds(change), type);
            }
        }

        // Client only: tells the host about a blow on a creature it owns.
        private static void ReportToOwner(Health health, HealthChange change, int amount, bool fromCreature)
        {
            if (CoopSession.State != CoopSessionState.Joined) return;

            Entity? owner = health.OwnerEntity;
            Actor? actor = owner == null ? null : owner.TryCast<Actor>();
            if (actor == null) return;

            if (!EnemyRegistry.TryGetId(actor, out EnemyNetId id)) return;

            // A leased brawl is fought here for real, the blow already landed.
            if (EngageWatch.IsBrawling(id)) return;

            // A driven creature's fight runs on the host, which takes its blows there.
            if (fromCreature && EnemyRegistry.IsDriven(id)) return;

            // The host has no brawl, so it must be told this blow may not kill.
            byte flags = fromCreature ? EnemyDamageMessage.FlagCreature : (byte)0;
            if (!fromCreature && PlayerAccess.TryGetPlayer(out Actor player)
                && player.CustomStats != null && player.CustomStats.IsInBrawl)
            {
                flags = EnemyDamageMessage.FlagBrawl;
            }

            if (fromCreature) CreatureBlowsSent++;

            foreach ((byte type, int part, bool first) in Split(change, amount))
            {
                RemoteEnemyHealth.ReportEnemyDamage(id, part, first ? change.PotentialPoiseDmg : 0f, first && Bleeds(change),
                    type, flags);
            }
        }

        // Host: a creature struck a claimed creature whose fight a client runs. That client reports the same fight, so a
        // copy fighting here too counts every exchange twice. Expected never to happen: the host ticks no far AI.
        private static void NoticeHostCopyFight(Health health, Entity? source)
        {
            Actor? striker = source == null ? null : source.TryCast<Actor>();
            if (striker == null || RemotePlayers.IsPlayersBody(striker)) return;

            Actor? defender = health.OwnerEntity == null ? null : health.OwnerEntity.TryCast<Actor>();
            if (defender == null || !EnemyRegistry.TryGetSessionId(defender, out EnemyNetId id)) return;
            if (!EnemyRegistry.ClaimedIds.Contains(id) || EnemyRegistry.DrivesOnHost(id) || EnemyEngagements.TryGetLease(id, out _)) return;

            HostCopyFights++;
            if (HostCopyFights == 1 || HostCopyFights % 25 == 0)
            {
                MelonLogger.Warning($"[Dmg] a creature fought {id} on the host while a client runs that fight; " +
                    "both machines count its blows. So far: " + HostCopyFights);
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

        /// <summary>Client: whether a blow came from a creature whose AI runs here, not on the host.</summary>
        private static bool IsACreatureTheClientRuns(Entity? source)
        {
            Actor? actor = source == null ? null : source.TryCast<Actor>();
            if (actor == null || RemotePlayers.IsPlayersBody(actor)) return false;

            return !EnemyRegistry.TryGetId(actor, out EnemyNetId id) || !EnemyRegistry.IsDriven(id);
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

            Span<int> raw = stackalloc int[3];
            if (values != null)
            {
                for (int index = 0; index < values.Count; index++)
                {
                    HealthChangeValue value = values[index];
                    if (!value.IsDamaging || value.Amount == 0) continue;

                    DamageStatDesc? type = value.DmgType;
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
            Span<int> share = stackalloc int[3];
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
