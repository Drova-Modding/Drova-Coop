using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Enemies
{
    /// <summary>
    /// Decides which creatures the host owns, hands out their handles, and on a client keeps the bodies
    /// those handles refer to. The host claims a creature only while it has a live body for it, so any
    /// failure falls back to both machines simulating it independently.
    /// </summary>
    internal sealed class EnemyRegistry : ICoopSystem
    {
        /// <summary>The distance at which an actor factory keeps a body loaded.</summary>
        private const float ClaimRange = 600f;

        /// <summary>Wider than <see cref="ClaimRange"/> so a creature on the boundary does not bind and release every few frames.</summary>
        private const float ReleaseRange = 700f;

        private const float SweepSeconds = 0.25f;

        /// <summary>Sweeps in a row that re-announce claims, to outlast a newcomer still finishing its handshake.</summary>
        private const int RebindRepeats = 12;

        /// <summary>Scene-authored creatures that are plain actors (Alraunes), by guid. Filled as their spots start.</summary>
        internal static readonly HashSet<string> AuthoredCreatures = new(StringComparer.Ordinal);

        // Grow-only: a reused handle would catch an unreliable packet still in flight for its old owner.
        private static readonly Dictionary<string, EnemyNetId> IdsByGuid = new(StringComparer.Ordinal);
        private static readonly Dictionary<EnemyNetId, string> GuidsById = new();
        private static readonly HashSet<EnemyNetId> ClaimedSet = new();

        // Paired by one spawn announcement rather than a guid, so the pairing cannot be re-established.
        private static readonly HashSet<EnemyNetId> SpawnedClaims = new();

        // What the far side was last told about who moves each claimed creature.
        private static readonly Dictionary<EnemyNetId, bool> Drives = new();

        // Held by reference, see TryResolveOnHost.
        private static readonly Dictionary<EnemyNetId, Actor> SpawnedBodies = new();

        // Guid lookups resolved once. An entity leaves the guid index only in OnDestroy, so a live entry is still the answer.
        private static readonly Dictionary<EnemyNetId, Actor> ResolvedBodies = new();
        private static readonly List<EnemyNetId> ToRelease = new();
        private static ushort _nextId;
        private static bool _exhausted;

        private static readonly Dictionary<EnemyNetId, string> BoundGuids = new();
        private static readonly Dictionary<EnemyNetId, Actor> BoundById = new();

        // Bodies this machine built, held by reference for the reason TryResolveOnHost explains.
        private static readonly HashSet<EnemyNetId> SpawnedBound = new();

        // Only creatures this mod made invincible, so a scripted invulnerable NPC is never restored as killable.
        private static readonly HashSet<EnemyNetId> ProtectedByUs = new();

        // Bound creatures whose protection was lifted for this player's own scripted brawl.
        private static readonly HashSet<EnemyNetId> LeaseBared = new();

        // The dead the host reported on joining. A creature dead before the join is never mentioned again.
        private static readonly HashSet<string> GraveyardGuids = new(StringComparer.Ordinal);

        // By guid because the save load and streaming meet a body before anything knows its handle.
        private static readonly HashSet<string> BoundByGuid = new(StringComparer.Ordinal);

        // Creatures the host answered alive from its save without claiming them.
        private static readonly HashSet<string> AliveByWordGuids = new(StringComparer.Ordinal);

        // Which bound creatures the host also drives. The rest run their own behavior here.
        private static readonly HashSet<EnemyNetId> Driven = new();

        // Drive statements kept apart from Driven, because one can arrive before its summon is paired.
        private static readonly Dictionary<EnemyNetId, bool> DriveStated = new();

        // Claims, releases and drive changes in arrival order: a release and a re-claim of one id must not swap.
        private static readonly Queue<CreatureOrder> IncomingCreatureOrders = new();

        private static float _sinceSweep;
        private static int _lastRosterCount;
        private static int _rebindsLeft;

        /// <summary>How many creatures the host currently owns.</summary>
        internal static int Claimed => ClaimedSet.Count;

        /// <summary>How many creatures this client is showing under the host's authority.</summary>
        internal static int Bound => BoundGuids.Count;

        /// <summary>Every bound body on this client, by handle. Read-only walks only.</summary>
        internal static Dictionary<EnemyNetId, Actor> BoundBodies => BoundById;

        /// <summary>Which creatures the host currently owns, the set every broadcaster walks. Empty on a client.</summary>
        internal static HashSet<EnemyNetId> ClaimedIds => ClaimedSet;

        /// <summary>Cheap pre-check for hooks that run for every body in the world.</summary>
        internal static bool HasAnyAlive => BoundByGuid.Count > 0 || AliveByWordGuids.Count > 0;

        /// <summary>How many creatures are alive only by the host's word.</summary>
        internal static int AliveByWord => AliveByWordGuids.Count;

        /// <summary>Runs the host or client half.</summary>
        void ICoopSystem.Update(float unscaledDeltaTime)
        {
            if (!CoopSession.IsLive)
            {
                Clear();
                return;
            }

            if (CoopSession.State == CoopSessionState.Joined)
            {
                DrainClientMessages();
                ReconcileBoundBodies();
                return;
            }

            // The old world's corpses would go out as binds of dead creatures, and lay out the teammates' new world.
            if (Core.WorldLoading) return;

            _sinceSweep += unscaledDeltaTime;
            if (_sinceSweep < SweepSeconds) return;

            _sinceSweep = 0f;

            Sweep();
        }

        /// <summary>
        /// Hands back every invincibility this mod set and forgets everything. The flag survives leaving a
        /// session, so skipping the restore leaves solo creatures unkillable.
        /// </summary>
        private static void Clear()
        {
            RestoreProtected();

            IdsByGuid.Clear();
            GuidsById.Clear();
            ClaimedSet.Clear();
            LeaseBared.Clear();
            SpawnedClaims.Clear();
            Drives.Clear();
            SpawnedBodies.Clear();
            ResolvedBodies.Clear();
            BoundGuids.Clear();
            BoundById.Clear();
            SpawnedBound.Clear();
            BoundByGuid.Clear();
            AliveByWordGuids.Clear();
            Driven.Clear();
            DriveStated.Clear();
            GraveyardGuids.Clear();
            _sinceSweep = 0f;
            _lastRosterCount = 0;
            _rebindsLeft = 0;
            _nextId = 0;
            _exhausted = false;
        }

        /// <summary>Looks up the body a handle refers to on this client.</summary>
        /// <returns>False when the handle is unknown or its body has not streamed in.</returns>
        internal static bool TryGetBody(EnemyNetId id, [NotNullWhen(true)] out Actor? actor)
        {
            return BoundById.TryGetValue(id, out actor) && actor != null;
        }

        /// <summary>Finds the handle for a creature bound on this client.</summary>
        internal static bool TryGetId(Actor actor, out EnemyNetId id)
        {
            id = EnemyNetId.None;

            if (actor == null) return false;

            foreach (KeyValuePair<EnemyNetId, Actor> bound in BoundById)
            {
                if (bound.Value != null && bound.Value.Pointer == actor.Pointer)
                {
                    id = bound.Key;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the handle for a creature on either end. Use it over the guid for per-creature agreement,
        /// since a spawned creature's guid differs on each machine.
        /// </summary>
        /// <returns>False when this creature is not part of the session.</returns>
        internal static bool TryGetSessionId(Actor actor, out EnemyNetId id)
        {
            id = EnemyNetId.None;

            if (actor == null) return false;

            if (CoopSession.State == CoopSessionState.Hosting)
            {
                string guid = GuidOf(actor);
                if (guid.Length == 0) return false;

                if (!IdsByGuid.TryGetValue(guid, out id)) return false;

                // A released creature keeps its handle but is simulated separately again.
                if (!ClaimedSet.Contains(id))
                {
                    id = EnemyNetId.None;
                    return false;
                }

                return true;
            }

            return TryGetId(actor, out id);
        }

        /// <summary>Finds the body a handle refers to on the host.</summary>
        /// <returns>False when the handle is unknown or the host has lost the body.</returns>
        internal static bool TryResolveOnHost(EnemyNetId id, [NotNullWhen(true)] out Actor? actor)
        {
            actor = null;

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return false;

            return TryResolveOnHost(entities, id, out actor);
        }

        /// <summary>
        /// Spawned creatures resolve by reference: for a prefab's second instance <c>GuidComponent.Awake</c>
        /// can mint a fresh guid after <c>Entity.Awake</c> registered the old one, so the guid lookup misses.
        /// </summary>
        private static bool TryResolveOnHost(EntityGameHandler entities, EnemyNetId id, [NotNullWhen(true)] out Actor? actor)
        {
            if (SpawnedBodies.TryGetValue(id, out Actor? spawned))
            {
                actor = spawned;
                return actor != null;
            }

            if (ResolvedBodies.TryGetValue(id, out Actor? cached) && cached != null)
            {
                actor = cached;
                return true;
            }

            actor = null;

            if (!GuidsById.TryGetValue(id, out string? guid)) return false;

            actor = entities.GetActorFromGuid(guid);
            if (actor == null) return false;

            ResolvedBodies[id] = actor;
            return true;
        }

        /// <summary>The actor's scene guid, or empty when it has none.</summary>
        internal static string GuidOf(Actor actor)
        {
            GuidComponent? guid = actor.GetGuidComponent();
            return guid == null ? string.Empty : guid.GuidString ?? string.Empty;
        }

        /// <summary>Records that a creature is dead, so a body streaming in later comes up as a corpse.</summary>
        internal static void RecordDead(string guid)
        {
            if (!string.IsNullOrEmpty(guid))
            {
                GraveyardGuids.Add(guid);

                // A stale entry would keep HasAnyAlive true and the save-health hook busy.
                AliveByWordGuids.Remove(guid);
            }
        }

        /// <summary>Whether the host has said this creature is dead.</summary>
        internal static bool IsKnownDead(string guid) => GraveyardGuids.Contains(guid);

        /// <summary>A savegame was loaded: the save says who is dead again.</summary>
        private static void ForgetGraveyard() => GraveyardGuids.Clear();

        /// <summary>
        /// The revive command's write, the one place the graveyard does not win. The caller vouches for the
        /// guid being authored.
        /// </summary>
        internal static void ForceAlive(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            GraveyardGuids.Remove(guid);
            AliveByWordGuids.Add(guid);
        }

        /// <summary>The guid a handle was bound under on this client.</summary>
        internal static bool TryGetBoundGuid(EnemyNetId id, [NotNullWhen(true)] out string? guid)
        {
            return BoundGuids.TryGetValue(id, out guid);
        }

        /// <summary>A claimed creature's guid, refused for runtime-spawned ones whose guid names it only here.</summary>
        internal static bool TryGetSharedGuid(EnemyNetId id, [NotNullWhen(true)] out string? guid)
        {
            guid = null;

            if (SpawnedClaims.Contains(id)) return false;

            return GuidsById.TryGetValue(id, out guid) && !string.IsNullOrEmpty(guid);
        }

        /// <summary>Whether the host has this creature alive, by claim or by its save's word, and has not since reported it dead.</summary>
        internal static bool IsKnownAlive(string guid)
        {
            return (BoundByGuid.Contains(guid) || AliveByWordGuids.Contains(guid)) && !GraveyardGuids.Contains(guid);
        }

        /// <summary>Whether this machine holds a session binding for the actor. Bound actors are placed only by the host's stream.</summary>
        internal static bool IsInSessionHere(string guid)
        {
            return !string.IsNullOrEmpty(guid) && BoundByGuid.Contains(guid);
        }

        /// <summary>
        /// Whether an actor is safe to read or drive. <c>LazyActor.LoadActor</c> registers the guid before
        /// <c>StartInitAsync</c> builds <c>MovementModule.DirectionModule</c>, so the modules exist but are unusable for several frames.
        /// </summary>
        internal static bool IsReady([NotNullWhen(true)] Actor? actor)
        {
            return actor != null && !actor.IsDestroyed && actor.IsInitialized;
        }

        /// <summary>Walks <c>EntityGameHandler</c>'s guid dictionary, the index handles resolve through, and claims or releases.</summary>
        private static void Sweep()
        {
            try
            {
                if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

                Il2CppSystem.Collections.Generic.Dictionary<string, Actor> byGuid = entities._entityGuidDictionary;
                if (byGuid == null) return;

                if (!PlayerAccess.TryGetPlayer(out _) && RemotePlayers.Positions.Count == 0) return;

                RebindForNewcomers(entities);

                // Arena mode shares no world creatures; summons are claimed as they spawn.
                if (CoopSession.SharesWorld)
                {
                    foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Actor> pair in byGuid)
                    {
                        Consider(pair);
                    }
                }

                ReviewWhoDrives(entities);

                ReleaseThoseOutOfReach(entities);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] sweeping the world for creatures to claim failed: " + e);
            }
        }

        /// <summary>
        /// Re-announces every existing claim when the roster changes, since a bind is sent once and a
        /// newcomer missed it. The far side keeps the newest bind, so repeats are harmless.
        /// </summary>
        private static void RebindForNewcomers(EntityGameHandler entities)
        {
            int roster = CoopSession.Players.Count;

            if (roster != _lastRosterCount)
            {
                _lastRosterCount = roster;

                _rebindsLeft = RebindRepeats;
            }

            if (_rebindsLeft <= 0) return;

            _rebindsLeft--;

            if (ClaimedSet.Count == 0) return;

            int sent = 0;

            foreach (EnemyNetId id in ClaimedSet)
            {
                // SpawnedEnemies re-announces spawned creatures. Only who moves one is restated, since
                // ReviewWhoDrives speaks only on change.
                if (SpawnedClaims.Contains(id))
                {
                    if (Drives.TryGetValue(id, out bool spawnedDrives))
                    {
                        SendEnemyDrive(id, spawnedDrives);
                    }

                    continue;
                }

                if (!GuidsById.TryGetValue(id, out string guid)) continue;
                if (!TryResolveOnHost(entities, id, out Actor? actor)) continue;

                Health health = actor.GetHealth();
                if (health == null) continue;

                // Who drives is sent as recorded, not recomputed, so it cannot disagree with the host's own
                // book in the band between claim and release range.
                if (!Drives.TryGetValue(id, out bool drives))
                {
                    drives = !EnemyEngagements.TryGetLease(id, out _) && CanDrive(actor.transform.position);
                    Drives[id] = drives;
                }

                SendBind(id, guid, health, drives);

                sent++;
            }

            if (sent > 0 && _rebindsLeft == RebindRepeats - 1)
            {
                MelonLogger.Msg($"[Enemy] telling the session about {sent} creature(s) already claimed here, " +
                    $"on each of the next {RebindRepeats} sweeps so a machine still joining does not miss them.");
            }
        }

        /// <summary>Decides whether one creature should be claimed, and claims it.</summary>
        /// <remarks>The guid is read last, since each read converts a string.</remarks>
        private static void Consider(Il2CppSystem.Collections.Generic.KeyValuePair<string, Actor> pair)
        {
            Actor actor = pair.Value;

            // Broadcasting a body whose movement has not started takes the whole broadcast down.
            if (!IsReady(actor)) return;

            if (RemotePlayers.IsPlayersBody(actor)) return;

            // A claim pins the body (SetStayActive), which would wake dormant boss waves like the Great Tusk fight.
            if (!actor.IsActiveInGame) return;

            if (!IsNearAnyPlayer(actor.transform.position, ClaimRange)) return;

            string guid = pair.Key;
            if (string.IsNullOrEmpty(guid)) return;

            if (IdsByGuid.TryGetValue(guid, out EnemyNetId existing) && ClaimedSet.Contains(existing)) return;

            if (!IsSharedIdentity(guid)) return;

            if (!TryTakeId(guid, out EnemyNetId id)) return;

            Health health = actor.GetHealth();
            if (health == null) return;

            ClaimedSet.Add(id);

            bool drives = CanDrive(actor.transform.position);
            Drives[id] = drives;

            SendBind(id, guid, health, drives);
        }

        private static void SendBind(EnemyNetId id, string guid, Health health, bool drives)
        {
            BindEnemy(
                id,
                guid,
                (ushort)Math.Clamp(health.CurHealth, 0, ushort.MaxValue),
                (ushort)Math.Clamp(health.MaxHealth, 0, ushort.MaxValue),
                health.IsDead,
                drives);
        }

        /// <summary>
        /// Re-sends who moves a claimed creature when the host's distance to it changes the answer. A stale
        /// "driven" freezes the client's copy, a stale "not driven" runs two AIs for one health.
        /// </summary>
        private static void ReviewWhoDrives(EntityGameHandler entities)
        {
            if (ClaimedSet.Count == 0) return;

            foreach (EnemyNetId id in ClaimedSet)
            {
                // Spawned creatures cannot take a bind, so their answer travels on its own message.
                bool spawned = SpawnedClaims.Contains(id);

                if (!GuidsById.TryGetValue(id, out string guid)) continue;
                if (!TryResolveOnHost(entities, id, out Actor? actor)) continue;

                bool told = Drives.TryGetValue(id, out bool last) && last;

                // Hysteresis between claim and release range. A leased creature is never driven: the
                // client's scripted brawl is the live copy.
                bool drives = !EnemyEngagements.TryGetLease(id, out _)
                    && CanDrive(actor.transform.position, told ? ReleaseRange : ClaimRange);

                if (Drives.ContainsKey(id) && told == drives) continue;

                Drives[id] = drives;

                MelonLogger.Msg($"[Enemy] {id} is now " +
                    (drives ? "the host's to move." : "the client's own to move again."));

                if (spawned)
                {
                    SendEnemyDrive(id, drives);
                    continue;
                }

                Health health = actor.GetHealth();
                if (health == null) continue;

                SendBind(id, guid, health, drives);
            }

        }

        /// <summary>Gives back everything the host can no longer speak for.</summary>
        private static void ReleaseThoseOutOfReach(EntityGameHandler entities)
        {
            ToRelease.Clear();

            foreach (EnemyNetId id in ClaimedSet)
            {
                if (!GuidsById.ContainsKey(id))
                {
                    ToRelease.Add(id);
                    continue;
                }

                // The broadcasters' resolver, so a spawned creature is not released the moment it is claimed.
                TryResolveOnHost(entities, id, out Actor? actor);

                if (actor == null)
                {
                    ToRelease.Add(id);
                    ReleaseEnemy(id, EnemyReleaseMessage.ReasonHostLostBody);
                    continue;
                }

                // Never released for distance: the spawn pairing happened once and cannot be re-claimed.
                if (SpawnedClaims.Contains(id)) continue;

                if (!IsNearAnyPlayer(actor.transform.position, ReleaseRange))
                {
                    ToRelease.Add(id);
                    ReleaseEnemy(id, EnemyReleaseMessage.ReasonOutOfRange);
                }
            }

            foreach (EnemyNetId id in ToRelease)
            {
                ClaimedSet.Remove(id);
                SpawnedClaims.Remove(id);
                Drives.Remove(id);
                SpawnedBodies.Remove(id);
                ResolvedBodies.Remove(id);
            }
        }

        /// <summary>Hands out a handle. Refuses rather than recycles once they run out.</summary>
        private static bool TryTakeId(string guid, out EnemyNetId id)
        {
            if (IdsByGuid.TryGetValue(guid, out id)) return true;

            if (_nextId > EnemyNetId.MaxAssignable)
            {
                if (!_exhausted)
                {
                    _exhausted = true;
                    MelonLogger.Warning("[Enemy] ran out of handles for creatures; the rest of this session's " +
                        "will not be synchronized. Handles are never reused, so this means a very long session.");
                }

                id = EnemyNetId.None;
                return false;
            }

            id = new EnemyNetId(_nextId++);
            IdsByGuid[guid] = id;
            GuidsById[id] = guid;
            return true;
        }

        /// <summary>
        /// Whether the host can drive this creature's body. Only near the host's own player, since a driven
        /// creature's far copy has its AI off and the host only ticks AI it is close to.
        /// </summary>
        private static bool CanDrive(Vector2 position, float range = ClaimRange)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor? local)) return false;

            Vector2 host = local.transform.position;

            return (host - position).sqrMagnitude <= range * range;
        }

        private static bool IsNearAnyPlayer(Vector2 position, float range)
        {
            return CanDrive(position, range) || RemotePlayers.IsAnyWithin(position, range);
        }

        private static void DrainClientMessages()
        {
            while (TryTakeCreatureOrder(out CreatureOrder order))
            {
                switch (order.Kind)
                {
                    case CreatureOrder.KindBind:
                        Bind(order.Bind);
                        break;

                    case CreatureOrder.KindRelease:
                        Release(order.Release.NetId);

                        // An ended world event despawned the host's creature, so the copy built here goes too.
                        if (order.Release.Reason == EnemyReleaseMessage.ReasonHostLostBody) SpawnedEnemies.Unbuild(order.Release.NetId);
                        break;

                    default:
                        ApplyDrive(order.Drive);
                        break;
                }
            }

            while (HostWorldState.TryTakeGraveyard(out EnemyGraveyardMessage chunk))
            {
                foreach (string guid in chunk.Guids)
                {
                    RecordDead(guid);
                    HostWorldState.RecordAnswered(guid);
                    RemoteEnemyHealth.QueueKnownDeath(guid);
                }
            }

            while (HostWorldState.TryTakeAlive(out EnemyAliveMessage alive))
            {
                foreach (string guid in alive.Guids)
                {
                    RecordAlive(guid);
                }
            }

            while (HostWorldState.TryTakeRevive(out EnemyReviveMessage revive))
            {
                foreach (string guid in revive.Guids)
                {
                    Revive(guid);
                }
            }
        }

        /// <summary>Takes in the host's revive decree, which overrules the graveyard.</summary>
        private static void Revive(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            if (ActorSpawnAccess.IsRuntimeCreated(guid)) return;

            ForceAlive(guid);
            RemoteEnemyDeath.ForgetEnemyDeath(guid);
            HostWorldState.RecordAnswered(guid);
            EnemyRebirth.AllowAgain(guid);
            EnemyRebirth.Request(guid);
        }

        /// <summary>
        /// Takes in the host's word that a local corpse is alive: refuses this save's kill and rebuilds the
        /// body as an ordinary unclaimed creature.
        /// </summary>
        private static void RecordAlive(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            // A fresher death broadcast wins over a crossing alive answer.
            if (GraveyardGuids.Contains(guid)) return;

            // The host's save has no record of a locally minted guid and reads that as alive.
            if (ActorSpawnAccess.IsRuntimeCreated(guid)) return;

            AliveByWordGuids.Add(guid);
            HostWorldState.RecordAnswered(guid);
            EnemyRebirth.Request(guid);
        }

        /// <summary>
        /// Collects every creature this world's save holds as dead, including camps cleared before any session.
        /// Reads the lazy-actor dictionary, which answers without the creature being loaded.
        /// </summary>
        internal static void CollectSaveDead(ISet<string> into)
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppDrova.Utilities.LazyLoading.LazyActor>
                handles = entities._lazyActorGuidDictionary;

            if (handles == null) return;

            foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Il2CppDrova.Utilities.LazyLoading.LazyActor>
                pair in handles)
            {
                Il2CppDrova.Utilities.LazyLoading.LazyActor lazy = pair.Value;
                if (lazy == null || lazy.IsDestroyed) continue;

                string guid = pair.Key;
                if (string.IsNullOrEmpty(guid)) continue;

                if (ActorSpawnAccess.IsRuntimeCreated(guid)) continue;

                // A revive this session outranks the save.
                if (IsKnownAlive(guid)) continue;

                if (!lazy.IsDead()) continue;

                into.Add(guid);
            }
        }

        private static void Bind(in EnemyBindMessage bind)
        {
            if (string.IsNullOrEmpty(bind.ActorGuid)) return;

            BoundGuids[bind.NetId] = bind.ActorGuid;
            BoundByGuid.Add(bind.ActorGuid);

            // A claim answers the alive question, so the asking stops.
            HostWorldState.RecordAnswered(bind.ActorGuid);

            SetDriven(bind.NetId, bind.HostDrives);

            if (bind.IsDeadAtBind)
            {
                RecordDead(bind.ActorGuid);

                // Laid out too, since a rebind of an already-seen body never reaches the lay-out path.
                RemoteEnemyHealth.QueueKnownDeath(bind.ActorGuid);
            }
        }

        /// <summary>Whether the host drives this creature's body, not merely its health.</summary>
        internal static bool IsDriven(EnemyNetId id) => Driven.Contains(id);

        /// <summary>
        /// Whether the host moves this claimed creature, asked on the host. An undriven creature's far copy
        /// hits its own player already, so forwarding the blow would bill it twice.
        /// </summary>
        internal static bool DrivesOnHost(EnemyNetId id)
        {
            return Drives.TryGetValue(id, out bool drives) && drives;
        }

        /// <summary>Takes in who moves a spawned creature, remembered even before its summon is paired.</summary>
        private static void ApplyDrive(in EnemyDriveMessage drive)
        {
            DriveStated[drive.NetId] = drive.HostDrives;

            if (!BoundGuids.ContainsKey(drive.NetId)) return;

            SetDriven(drive.NetId, drive.HostDrives);
        }

        private static void SetDriven(EnemyNetId id, bool driven)
        {
            if (driven)
            {
                Driven.Add(id);
            }
            else
            {
                Driven.Remove(id);
            }
        }

        private static void Release(EnemyNetId id)
        {
            if (BoundById.TryGetValue(id, out Actor? actor) && actor != null)
            {
                Restore(id, actor);
            }

            if (BoundGuids.TryGetValue(id, out string? guid))
            {
                BoundByGuid.Remove(guid);
            }

            BoundGuids.Remove(id);
            BoundById.Remove(id);
            SpawnedBound.Remove(id);
            Driven.Remove(id);
            DriveStated.Remove(id);
            ProtectedByUs.Remove(id);
        }

        /// <summary>Reconciles bound handles with the bodies that exist, every frame, since bodies stream without notice.</summary>
        private static void ReconcileBoundBodies()
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            foreach (KeyValuePair<EnemyNetId, string> bound in BoundGuids)
            {
                // Built here, so kept by reference: its guid may not resolve (see TryResolveOnHost).
                if (SpawnedBound.Contains(bound.Key))
                {
                    // A summon of this client's own leased fight must take this player's blows.
                    if (BoundById.TryGetValue(bound.Key, out Actor? built) && built != null)
                    {
                        Bare(bound.Key, built);
                        continue;
                    }

                    BoundById.Remove(bound.Key);
                    continue;
                }

                if (BoundById.TryGetValue(bound.Key, out Actor? known) && known != null)
                {
                    Bare(bound.Key, known);
                    continue;
                }

                Actor? actor = entities.GetActorFromGuid(bound.Value);

                if (actor == null)
                {
                    // Chunk unloaded here. The binding stands.
                    BoundById.Remove(bound.Key);
                    continue;
                }

                BoundById[bound.Key] = actor;

                Protect(bound.Key, actor);
                Bare(bound.Key, actor);
            }
        }

        /// <summary>
        /// Lifts a bound creature's protection while this player's scripted brawl runs against it, so the
        /// local fight can resolve and set the quest's outcome.
        /// </summary>
        private static void Bare(EnemyNetId id, Actor actor)
        {
            try
            {
                bool leased = EngageWatch.IsBrawling(id);

                if (leased && ProtectedByUs.Contains(id))
                {
                    Restore(id, actor);
                    LeaseBared.Add(id);
                    return;
                }

                if (!leased && LeaseBared.Remove(id))
                {
                    Protect(id, actor);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] adjusting a creature's protection for a brawl failed: " + e);
            }
        }

        /// <summary>
        /// Stops this machine deciding a bound creature's health. Invincibility is read inside the health
        /// calculation, so it holds where a prefix would be inlined away, and hit effects still play.
        /// </summary>
        private static void Protect(EnemyNetId id, Actor actor)
        {
            try
            {
                Health health = actor.GetHealth();
                if (health == null) return;

                if (!health.IsInvincible)
                {
                    ProtectedByUs.Add(id);
                    health.SetInvinciblity(true);
                }

                if (IsKnownDead(BoundGuids[id]))
                {
                    if (!health.IsDead)
                    {
                        RemoteEnemyHealth.QueueKnownDeath(BoundGuids[id]);
                    }

                    return;
                }

                // Alive on the host but dead here: a death cannot be undone in place, so the body is rebuilt.
                if (health.IsDead)
                {
                    EnemyRebirth.Request(BoundGuids[id]);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] write-protecting a bound creature failed: " + e);
            }
        }

        private static void Restore(EnemyNetId id, Actor actor)
        {
            if (!ProtectedByUs.Remove(id)) return;

            try
            {
                Health health = actor.GetHealth();
                if (health != null)
                {
                    health.SetInvinciblity(false);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] handing a creature's health back failed: " + e);
            }
        }

        private static void RestoreProtected()
        {
            // Walks the bodies, because Restore removes from the protected set.
            foreach (KeyValuePair<EnemyNetId, Actor> bound in BoundById)
            {
                if (bound.Value != null)
                {
                    Restore(bound.Key, bound.Value);
                }
            }

            ProtectedByUs.Clear();
        }

        /// <summary>
        /// Claims a creature spawned at runtime here, keyed by its local guid. The handle crosses the wire,
        /// never the guid.
        /// </summary>
        /// <returns>False when it has no usable local identity or handles have run out.</returns>
        internal static bool TryClaimSpawned(Actor actor, out EnemyNetId id)
        {
            id = EnemyNetId.None;

            string guid = GuidOf(actor);
            if (guid.Length == 0) return false;

            if (!TryTakeId(guid, out id)) return false;

            ClaimedSet.Add(id);
            SpawnedClaims.Add(id);
            SpawnedBodies[id] = actor;

            // Claimed but not bound: the spawn announcement pairs it. Who moves it is sent now, on the same
            // ordered channel ahead of that announcement.
            bool drives = CanDrive(actor.transform.position);
            Drives[id] = drives;

            SendEnemyDrive(id, drives);

            return true;
        }

        /// <summary>
        /// Records a body built here for a host spawn. Driven by default when no statement arrived, since
        /// the next review corrects that but nothing corrects a permanently undriven copy.
        /// </summary>
        internal static void BindSpawnedBody(EnemyNetId id, Actor body)
        {
            if (body == null) return;

            string guid = GuidOf(body);
            if (guid.Length == 0) return;

            BoundGuids[id] = guid;
            BoundByGuid.Add(guid);
            BoundById[id] = body;
            SpawnedBound.Add(id);

            SetDriven(id, !DriveStated.TryGetValue(id, out bool stated) || stated);

            Protect(id, body);
        }

        /// <summary>Whether this guid names the same creature on every machine. See <see cref="EnemyDeathWatch"/>.</summary>
        internal static bool IsSharedIdentity(string guid)
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return false;

            // API-spawned creatures are shared through SpawnedEnemies instead.
            if (ActorSpawnAccess.IsRuntimeCreated(guid)) return false;

            if (entities.GetLazyActorFromGuid(guid) != null) return true;

            // An Alraune is an authored plain actor, not a lazy one, so its scene guid is shared too.
            return AuthoredCreatures.Contains(guid);
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<EnemyBindMessage>(MessageIds.EnemyBind,
                (_, m) => CoopSession.QueueFromHost(new CreatureOrder(m), IncomingCreatureOrders, CoopSession.MaxPendingFromHost, "creature claims"));
            CoopSession.Register<EnemyReleaseMessage>(MessageIds.EnemyRelease,
                (_, m) => CoopSession.QueueFromHost(new CreatureOrder(m), IncomingCreatureOrders, CoopSession.MaxPendingFromHost, "creature releases"));
            CoopSession.Register<EnemyDriveMessage>(MessageIds.EnemyDrive,
                (_, m) => CoopSession.QueueFromHost(new CreatureOrder(m), IncomingCreatureOrders, CoopSession.MaxPendingFromHost, "creature drive changes"));
        }

        void ICoopSystem.EndSession()
        {
            IncomingCreatureOrders.Clear();
        }

        /// <summary>Claims a creature and tells every client about it.</summary>
        private static void BindEnemy(EnemyNetId id, string actorGuid, ushort health, ushort maxHealth, bool isDead, bool hostDrives)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            byte flags = isDead ? EnemyBindMessage.FlagDeadAtBind : (byte)0;

            if (hostDrives)
            {
                flags |= EnemyBindMessage.FlagHostDrives;
            }

            NetworkAccess.SendToAll(new EnemyBindMessage
            {
                NetId = id,
                ActorGuid = actorGuid,
                Health = health,
                MaxHealth = maxHealth,
                Flags = flags,
            });
        }

        /// <summary>
        /// Tells every client whether the host moves one spawned creature's body. Same ordered channel as the
        /// spawn and summon bind, so it never overtakes them.
        /// </summary>
        private static void SendEnemyDrive(EnemyNetId id, bool hostDrives)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyDriveMessage
            {
                NetId = id,
                HostDrives = hostDrives,
            });
        }

        /// <summary>Gives a creature back and tells every client.</summary>
        private static void ReleaseEnemy(EnemyNetId id, byte reason)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            NetworkAccess.SendToAll(new EnemyReleaseMessage
            {
                NetId = id,
                Reason = reason,
            });
        }

        /// <summary>Takes the next claim, release or drive change, in the order the host sent them.</summary>
        private static bool TryTakeCreatureOrder(out CreatureOrder order)
            => IncomingCreatureOrders.TryDequeue(out order);

        void ICoopSystem.ForgetWorld(bool loaded, bool keepHaulBindings)
        {
            ForgetGraveyard();

            // Lifts write-protection, or a creature stays invincible into a solo save.
            if (!loaded) Clear();
        }
    }
}
