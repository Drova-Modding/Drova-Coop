using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Reports non-player deaths to the session, from the health funnel <see cref="DamageWatch"/> already patches.
    /// Death is checked before the non-damage guard because <c>SetHealth</c> kills with a zero <c>PotentialChange</c>.
    /// </summary>
    internal static class EnemyDeathWatch
    {
        /// <summary>Deaths reported to the session.</summary>
        internal static int Reported { get; private set; }

        /// <summary>Deaths swallowed as this machine's own echo.</summary>
        internal static int SuppressedAsEcho { get; private set; }

        /// <summary>Reported deaths caused by a teammate's relayed blow.</summary>
        internal static int FromTeammatesBlow { get; private set; }

        /// <summary>Called when a session ends.</summary>
        internal static void ResetCounters()
        {
            Reported = 0;
            SuppressedAsEcho = 0;
            FromTeammatesBlow = 0;
        }

        /// <summary>Reports this health change as a death when it was one. Runs for every health change, so guards go cheapest first.</summary>
        internal static void Notice(Health health, HealthChangeArgs changeArgs)
        {
            if (!health.IsDead) return;

            // Applying a reported death sets health to zero, which looks like a fresh kill.
            if (RemoteApply<EnemyHealthScope>.InProgress)
            {
                SuppressedAsEcho++;
                return;
            }

            if (!CoopSession.IsLive) return;

            Actor? actor = health.OwnerEntity?.TryCast<Actor>();
            if (actor == null) return;

            // Players die through DownedState, and a puppet's health comes from the wire.
            if (RemotePlayers.IsPlayersBody(actor)) return;

            string guid = actor.GetGuidComponent()?.GuidString ?? string.Empty;
            if (string.IsNullOrEmpty(guid)) return;

            // Runtime-spawned creatures get a fresh Guid.NewGuid() that no other machine knows.
            if (!IsSharedIdentity(guid)) return;

            // A save corpse streaming in, not a kill.
            bool isReconcile = changeArgs != null && changeArgs.IsLoadingArg();

            if (isReconcile)
            {
                // The host announces its save corpses. A client's are asked about by HostWorldState, and reporting
                // them here would let the host answer "dead" from its own dead set.
                if (CoopSession.State == CoopSessionState.Joined) return;
            }
            else
            {
                // A fresh kill overrides an earlier "known alive" from the host.
                EnemyRegistry.RecordDead(guid);
            }

            CoopSession.ReportEnemyDeath(guid, isReconcile);
            Reported++;

            if (RemoteApply<EnemyBlowScope>.InProgress)
            {
                FromTeammatesBlow++;

                // First and then every tenth.
                if (FromTeammatesBlow == 1 || FromTeammatesBlow % 10 == 0)
                {
                    MelonLogger.Msg("[Enemy] a teammate's blow killed something here and the death was " +
                        $"announced rather than swallowed as an echo. Announced so far: {FromTeammatesBlow}.");
                }
            }
        }

        /// <summary>
        /// Whether this guid names the same creature on every machine. Errs towards no: a wrongly accepted guid
        /// is held forever on the far side waiting for a body.
        /// </summary>
        private static bool IsSharedIdentity(string guid)
        {
            if (EnemyRegistry.IsSharedIdentity(guid)) return true;

            // The rest only sorts refusals for the counters.
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return false;

            // Another mod's spawn, shared through SpawnedEnemies instead.
            if (ActorSpawnAccess.IsRuntimeCreated(guid))
            {
                SpawnedElsewhere++;

                return false;
            }

            Unnameable++;

            if (Unnameable == 1)
            {
                MelonLogger.Msg("[Enemy] something died that no other machine can name - spawned at runtime " +
                    "rather than authored into the scene, so its death stays local. Count is in coop_status.");
            }

            return false;
        }

        /// <summary>Deaths not shared because the creature was spawned at runtime.</summary>
        internal static int Unnameable { get; private set; }

        /// <summary>Of those, creatures another mod spawned through the API, which <see cref="SpawnedEnemies"/> covers.</summary>
        internal static int SpawnedElsewhere { get; private set; }
    }
}
