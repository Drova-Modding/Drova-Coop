using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Notices when something that is not a player dies here, and tells everybody.
    ///
    /// This is the first thing about the world itself that crosses. Every machine runs its own copy of
    /// every creature, and until they share something they disagree about all of it. Death is the cheapest
    /// thing to agree on and the most necessary, because it is the only one that is permanent: health can
    /// be wrong for a second and correct itself, whereas a creature killed on one machine and left standing
    /// on the other stays that way, gets written into both saves, and is still wrong tomorrow.
    ///
    /// It rides on the health funnel <see cref="DamageWatch"/> already owns rather than adding a patch of
    /// its own. Two patches on one method are two things to keep in step for no benefit, and the funnel is
    /// the right place for the same reason it was the right place for player damage: <c>ChangeHealth</c>
    /// and <c>SetHealth</c> are independent write paths and both are inlined at some call sites, so only
    /// the private method they share sees everything.
    ///
    /// Death does not always arrive as damage. <c>SetHealth</c> builds its change with the source-only
    /// constructor, which leaves <c>PotentialChange</c> at zero - so this has to be asked before the guard
    /// that discards non-damaging changes, or a scripted kill would never be reported.
    /// </summary>
    internal static class EnemyDeathWatch
    {
        private static int _reported;
        private static int _suppressed;
        private static int _fromTeammatesBlow;

        /// <summary>How many deaths this machine told the session about. For <c>coop_status</c>.</summary>
        internal static int Reported => _reported;

        /// <summary>
        /// How many were swallowed as this machine's own echo. For <c>coop_status</c>, and it earned its
        /// line: a guard that silently swallowed the one event this subsystem exists to carry left
        /// nothing at all in a hundred and fifty kilobytes of log.
        /// </summary>
        internal static int SuppressedAsEcho => _suppressed;

        /// <summary>How many of the reported ones were killed by a teammate's relayed blow.</summary>
        internal static int FromTeammatesBlow => _fromTeammatesBlow;

        /// <summary>Forgets the counters. Called when a session ends.</summary>
        internal static void ResetCounters()
        {
            _reported = 0;
            _suppressed = 0;
            _fromTeammatesBlow = 0;
        }

        /// <summary>
        /// Reports this health change as a death when that is what it was.
        ///
        /// Called from inside the funnel postfix for every health change in the game, so the guards are
        /// ordered cheapest first and the common answer - a change that did not kill anything - costs one
        /// boolean read.
        /// </summary>
        /// <param name="health">The health that just changed.</param>
        /// <param name="changeArgs">What the game is about to tell its own listeners.</param>
        internal static void Notice(Health health, HealthChangeArgs changeArgs)
        {
            // Not dead is the overwhelmingly common case, and it is one field.
            if (!health.IsDead) return;

            // The write this mod just made, arriving back through the funnel it was always going to arrive
            // back through. Applying a reported death sets health to zero, which lands here looking exactly
            // like a fresh kill.
            if (RemoteApply<EnemyHealthScope>.InProgress)
            {
                _suppressed++;
                return;
            }

            if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined) return;

            Actor? actor = health.OwnerEntity?.TryCast<Actor>();
            if (actor == null) return;

            // Players die through their own path - they do not die at all while a session is live, see
            // DownedState - and a puppet's health is written from the wire, so a puppet reaching zero here
            // would be this machine reporting a death it invented.
            if (IsAPlayersBody(actor)) return;

            string guid = actor.GetGuidComponent()?.GuidString ?? string.Empty;
            if (string.IsNullOrEmpty(guid)) return;

            // A creature spawned at runtime cannot be named, and its guid does not admit it. Wave
            // spawners and summons instantiate a prefab whose own guid string is empty, so
            // `GuidComponent.Awake` mints one with `Guid.NewGuid()` - which is a perfectly well-formed
            // 36-character guid that means nothing on any other machine. Length proves nothing here;
            // every guid in the game is 36 characters by the time anything can read it.
            //
            // What separates the two is where the guid came from. A creature authored into a scene is
            // streamed in by a LazyActor, which holds the authored guid and stamps it onto the body it
            // creates - so a guid the lazy table knows was probably read out of a scene asset both machines
            // have. Probably, not certainly: a mod that spawns through the API creates its own handles, and
            // those land in the same table under a guid minted here. See IsSharedIdentity.
            if (!IsSharedIdentity(guid)) return;

            // A corpse the save already knew about, arriving as the world streams in around this player.
            // It must be told apart from a kill, because a body restored from a save must not replay its
            // death - and because who gets to say it depends on whose save is speaking.
            bool isReconcile = changeArgs != null && changeArgs.IsLoadingArg();

            if (isReconcile)
            {
                // The host's save is the world's, so its corpses are announced: the join-time graveyard
                // only covers what died in a session, and this is how the host's older kills reach a
                // client as chunks stream in on the host's side.
                //
                // A client's save corpse is a question, not an announcement. HostWorldState is already
                // asking the host about exactly this guid - and reporting it as a death used to answer
                // that question before it was asked: the report put the guid in the host's dead set, the
                // host answered "dead" from that set moments later, and where the host had built a body
                // it killed a creature its own world had alive. The one mechanism defeated the other,
                // and it is the asking that carries the authority.
                if (CoopSession.State == CoopSessionState.Joined) return;
            }
            else
            {
                // A fresh kill outranks anything the host said about this creature earlier. Without this,
                // a creature revived on the host's word and then killed here stays "known alive", and the
                // next time its chunk streams in the save's record of this kill is refused - a creature
                // standing up again on the machine that killed it.
                EnemyRegistry.RecordDead(guid);
            }

            CoopSession.ReportEnemyDeath(guid, isReconcile);
            _reported++;

            if (RemoteApply<EnemyBlowScope>.InProgress)
            {
                _fromTeammatesBlow++;

                // The line whose ABSENCE is the signature of the bug this split fixed: a teammate's blow
                // landing here, a creature dying, and nobody saying so. First and then every tenth,
                // because a party clearing a camp lands a lot of these.
                if (_fromTeammatesBlow == 1 || _fromTeammatesBlow % 10 == 0)
                {
                    MelonLogger.Msg("[Enemy] a teammate's blow killed something here and the death was " +
                        $"announced rather than swallowed as an echo. Announced so far: {_fromTeammatesBlow}.");
                }
            }
        }

        /// <summary>
        /// Whether this guid names the same creature on every machine.
        ///
        /// Two tests, and the second is the one that was missing. The lazy-actor table is the game's own
        /// index of the guids its scenes were authored with, so a guid found there was read out of an asset
        /// both machines have - unless a mod put it there. Anything spawned through the API's lazy path
        /// gets a guid from <c>Guid.NewGuid</c> and a handle registered in that same table, so the table
        /// alone answers yes for a creature no other machine has ever heard of. The API is asked which of
        /// the two this is, because it is the only thing that knows.
        ///
        /// Erring towards silence. A creature this test rejects simply does not replicate its death,
        /// which leaves the world exactly as divergent as it was before any of this existed. A creature it
        /// wrongly accepted sends a guid nobody can resolve, and the far side holds it forever waiting for a
        /// body that is never going to exist - filling the queue of unapplied deaths behind it.
        /// </summary>
        private static bool IsSharedIdentity(string guid)
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return false;

            // Invented here, whatever the table says. A creature spawned by another mod is shared through
            // SpawnedEnemies instead, which replicates the spawn rather than the name.
            if (ActorSpawnAccess.IsRuntimeCreated(guid))
            {
                _spawnedElsewhere++;

                return false;
            }

            if (entities.GetLazyActorFromGuid(guid) != null) return true;

            // Counted rather than announced per death. How much of the world is spawned at runtime rather
            // than authored is the ceiling on how much of it can ever be replicated by name, and it is
            // worth a number instead of an assumption - but a line per dying critter would bury a session.
            _unnameable++;

            if (_unnameable == 1)
            {
                MelonLogger.Msg("[Enemy] something died that no other machine can name - spawned at runtime " +
                    "rather than authored into the scene, so its death stays local. Count is in coop_status.");
            }

            return false;
        }

        /// <summary>How many deaths could not be shared because the creature was spawned at runtime.</summary>
        internal static int Unnameable => _unnameable;

        /// <summary>
        /// How many of those were creatures another mod spawned through the API. Told apart from the rest
        /// because these have a way out - <see cref="SpawnedEnemies"/> replicates the spawn - so a count that
        /// climbs while no spawns are announced says the two mods are not talking to each other.
        /// </summary>
        internal static int SpawnedElsewhere => _spawnedElsewhere;

        private static int _unnameable;
        private static int _spawnedElsewhere;

        /// <summary>
        /// Whether this body belongs to somebody playing - the person at this machine, or one of the
        /// remote players being shown here.
        /// </summary>
        private static bool IsAPlayersBody(Actor actor)
        {
            if (RemotePlayers.IsPuppet(actor)) return true;

            // By pointer, since two proxies over one native actor are different managed objects.
            return PlayerAccess.TryGetPlayer(out Actor localPlayer)
                && localPlayer != null
                && localPlayer.Pointer == actor.Pointer;
        }
    }
}
