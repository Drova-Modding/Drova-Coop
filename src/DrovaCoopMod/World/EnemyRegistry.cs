using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Decides which creatures the host owns, hands out the handles for them, and on a client keeps the
    /// bodies those handles refer to.
    ///
    /// Authority follows presence, and that rule is what makes every failure recoverable. The host
    /// claims a creature only while it can resolve a live body for it. The moment it cannot - the chunk
    /// unloaded there, the creature was destroyed, the world reloaded - it releases, and both machines go
    /// back to simulating that creature independently, which is the state the mod shipped with. So a bug
    /// here costs a creature whose health is not shared. It cannot cost a creature that is invincible on
    /// one screen with nobody able to kill it, which is the failure that would follow the player into
    /// their single-player save.
    ///
    /// Discovery is a sweep, not a patch. The obvious hook - something firing as each actor registers -
    /// is both an inlining risk and a trap: the game clears the listeners on that event every time a world
    /// loads, so a subscription made once is silently dropped afterwards. Reading the handler's own guid
    /// dictionary a few times a second costs a walk over a few hundred entries and cannot be undone by
    /// anything.
    /// </summary>
    internal static class EnemyRegistry
    {
        /// <summary>
        /// How far from a player a creature is claimed.
        ///
        /// The game's own number: this is the distance at which an actor factory keeps a body loaded, so
        /// claiming anything further would be claiming creatures the recipient does not have and is about
        /// to swap out for a placeholder.
        /// </summary>
        private const float ClaimRange = 600f;

        /// <summary>
        /// Claimed slightly further than it is released, so a creature walking along the boundary does not
        /// bind and release every few frames - each of which is a reliable message to every client.
        /// </summary>
        private const float ReleaseRange = 700f;

        /// <summary>How often the world is swept for creatures to claim.</summary>
        private const float SweepSeconds = 0.25f;

        // Host state. Grow-only: a handle is never handed back to the pool, because an unreliable packet
        // still in flight would land on whatever inherited its number.
        private static readonly Dictionary<string, EnemyNetId> _idsByGuid = new(StringComparer.Ordinal);
        private static readonly Dictionary<EnemyNetId, string> _guidsById = new();
        private static readonly HashSet<EnemyNetId> _claimed = new();

        // Of those, the ones that exist only because somebody spawned them. Held apart because the pairing
        // with the far side's copy is one announcement rather than a name, so it cannot be re-established.
        private static readonly HashSet<EnemyNetId> _spawnedClaims = new();

        // What the far side was last told about who moves each claimed creature. Kept because the answer
        // is a distance and both ends of it walk around; see ReviewWhoDrives.
        private static readonly Dictionary<EnemyNetId, bool> _drives = new();

        // And their bodies, by reference. See TryResolveOnHost for why a spawned creature cannot be looked
        // up by guid the way an authored one can.
        private static readonly Dictionary<EnemyNetId, Actor> _spawnedBodies = new();
        private static readonly List<EnemyNetId> _toRelease = new();
        private static ushort _nextId;
        private static bool _exhausted;

        // Client state. The body is looked up rather than held across frames where possible; what is held
        // is keyed by handle because that is what the wire uses.
        private static readonly Dictionary<EnemyNetId, string> _boundGuids = new();
        private static readonly Dictionary<EnemyNetId, Actor> _boundBodies = new();

        // Of those, the ones this machine built rather than found. Their bodies are held rather than
        // re-resolved, for the reason TryResolveOnHost explains.
        private static readonly HashSet<EnemyNetId> _spawnedBound = new();

        // Whose invincibility this mod turned on, and therefore whose it may turn off again. Not every
        // bound creature: a scripted invulnerable NPC was invincible before this mod touched it, and
        // restoring it as killable at the end of a session would leave the player's own world wrong in a
        // way they would never connect to co-op.
        private static readonly HashSet<EnemyNetId> _protectedByUs = new();

        // Bound creatures whose protection was lifted for this player's own scripted brawl. See Bare.
        private static readonly HashSet<EnemyNetId> _leaseBared = new();

        // The dead, by guid, as told by the host on joining. Consulted as bodies stream in, because a
        // creature that was already dead before this client arrived is never mentioned again.
        private static readonly HashSet<string> _graveyard = new(StringComparer.Ordinal);

        // Every guid the host has bound, so a creature can be asked about by guid rather than by handle.
        // The wire speaks in handles, but everything that meets a body before the session does - the save
        // load, the streaming system - only has a guid to go on.
        private static readonly HashSet<string> _boundByGuid = new(StringComparer.Ordinal);

        // Creatures the host has said are alive without claiming them - answered from its save, for a
        // corpse standing nowhere near it. Kept apart from the bound set because it means less: it is a
        // verdict about the world, not a body the host has. It is enough to refuse this machine's save
        // its kill and to ask for a rebuild, which is all "alive" ever needed a bind for.
        private static readonly HashSet<string> _aliveByWord = new(StringComparer.Ordinal);

        // Which of the bound creatures the host also drives. The rest keep running their own behavior
        // here, which is correct when the host is nowhere near them.
        private static readonly HashSet<EnemyNetId> _driven = new();

        // What the host has said about who moves each spawned creature, kept apart from _driven because
        // the two arrive on different clocks. A drive statement can land before its creature is paired -
        // the claim, the statement and the pairing all leave the host in one burst - and a summon pairing
        // can sit in SummonWatch's waiting list for seconds after that. Folding the statement into
        // _driven at arrival would lose it for a creature not bound yet, and defaulting at pairing would
        // stomp a statement that arrived first.
        private static readonly Dictionary<EnemyNetId, bool> _driveStated = new();

        /// <summary>
        /// How many sweeps in a row a newcomer is told about what is already claimed.
        ///
        /// One per sweep, so this is a quarter-second apart. Enough of them to outlast a joining machine
        /// still finishing its handshake; see RebindForNewcomers.
        /// </summary>
        private const int RebindRepeats = 12;

        private static float _sinceSweep;
        private static int _lastRosterCount;
        private static int _rebindsLeft;

        /// <summary>How many creatures the host currently owns. For <c>coop_status</c>.</summary>
        internal static int Claimed => _claimed.Count;

        /// <summary>How many creatures this client is showing under the host's authority.</summary>
        internal static int Bound => _boundGuids.Count;

        /// <summary>
        /// Runs the half of this that belongs to whichever end this machine is. Call once per frame from a
        /// loaded world.
        /// </summary>
        /// <param name="unscaledDeltaTime">Seconds since the last frame.</param>
        internal static void Update(float unscaledDeltaTime)
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

            _sinceSweep += unscaledDeltaTime;
            if (_sinceSweep < SweepSeconds) return;

            _sinceSweep = 0f;

            Sweep();
        }

        /// <summary>
        /// Gives every creature this machine write-protected back to itself and forgets everything.
        ///
        /// The restore is the important half. Invincibility is a plain runtime flag with nothing
        /// saving it, so it does not survive a restart - but it does survive leaving a session, and a
        /// player who quits co-op and carries on alone would find the creatures around them unkillable
        /// until they reloaded.
        /// </summary>
        internal static void Clear()
        {
            RestoreProtected();

            _idsByGuid.Clear();
            _guidsById.Clear();
            _claimed.Clear();
            _leaseBared.Clear();
            _spawnedClaims.Clear();
            _drives.Clear();
            _spawnedBodies.Clear();
            _boundGuids.Clear();
            _boundBodies.Clear();
            _spawnedBound.Clear();
            _boundByGuid.Clear();
            _aliveByWord.Clear();
            _driven.Clear();
            _driveStated.Clear();
            _graveyard.Clear();
            _sinceSweep = 0f;
            _lastRosterCount = 0;
            _rebindsLeft = 0;
            _nextId = 0;
            _exhausted = false;
        }

        /// <summary>
        /// Looks up the body a handle refers to on this client, when there is one.
        /// </summary>
        /// <param name="id">The handle.</param>
        /// <param name="actor">The body.</param>
        /// <returns>False when the handle is unknown or its body has not streamed in.</returns>
        internal static bool TryGetBody(EnemyNetId id, out Actor actor)
        {
            return _boundBodies.TryGetValue(id, out actor!) && actor != null;
        }

        /// <summary>
        /// Every creature this client is showing under the host's authority, by handle. For read-only
        /// walks; nothing here may bind or release.
        /// </summary>
        internal static IReadOnlyDictionary<EnemyNetId, Actor> BoundBodies => _boundBodies;

        /// <summary>Which creatures the host currently owns, the set every broadcaster walks. Empty on a client.</summary>
        internal static IReadOnlyCollection<EnemyNetId> ClaimedIds => _claimed;

        /// <summary>
        /// Finds the handle for a creature this client is showing under the host's authority.
        /// </summary>
        /// <param name="actor">The body.</param>
        /// <param name="id">Its handle.</param>
        /// <returns>False when this creature is not bound.</returns>
        internal static bool TryGetId(Actor actor, out EnemyNetId id)
        {
            id = EnemyNetId.None;

            if (actor == null) return false;

            foreach (KeyValuePair<EnemyNetId, Actor> bound in _boundBodies)
            {
                // By pointer, since two proxies over one native actor are different managed objects.
                if (bound.Value != null && bound.Value.Pointer == actor.Pointer)
                {
                    id = bound.Key;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Finds the handle for a creature whichever end this machine is.
        ///
        /// The handle rather than the guid, for anything the two machines have to agree about per
        /// creature. A guid is only shared for creatures the scenes were authored with; a creature another
        /// mod spawned has a different guid at each end and the same handle, which is the whole point of
        /// handing one out. Anything keyed by guid silently stops working for those.
        /// </summary>
        /// <param name="actor">The body.</param>
        /// <param name="id">Its handle.</param>
        /// <returns>False when this creature is not part of the session.</returns>
        internal static bool TryGetSessionId(Actor actor, out EnemyNetId id)
        {
            id = EnemyNetId.None;

            if (actor == null) return false;

            if (CoopSession.State == CoopSessionState.Hosting)
            {
                string guid = actor.GetGuidComponent()?.GuidString ?? string.Empty;
                if (string.IsNullOrEmpty(guid)) return false;

                if (!_idsByGuid.TryGetValue(guid, out id)) return false;

                // Claimed, not merely once handed a number. A handle is never given back to the pool, so a
                // creature the host released still has one - and speaking for it would be speaking for a
                // creature both machines are simulating separately again.
                if (!_claimed.Contains(id))
                {
                    id = EnemyNetId.None;
                    return false;
                }

                return true;
            }

            return TryGetId(actor, out id);
        }

        /// <summary>
        /// Finds the body a handle refers to on the host.
        /// </summary>
        /// <param name="id">The handle.</param>
        /// <param name="actor">The body.</param>
        /// <returns>False when the handle is unknown or the host has lost the body.</returns>
        internal static bool TryResolveOnHost(EnemyNetId id, out Actor actor)
        {
            actor = null!;

            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return false;

            return TryResolveOnHost(entities, id, out actor);
        }

        /// <summary>
        /// The same, for a caller that already has the handler.
        ///
        /// A spawned creature is held by reference, and that is not an optimization. Resolving one
        /// through the guid dictionary does not reliably work: <c>Entity.Awake</c> registers the body under
        /// whatever <c>GuidString</c> reads at that moment, and <c>GuidComponent.Awake</c> is a separate
        /// Awake on the same object with no defined order between them - so it can run afterwards, and for
        /// the second instance of a prefab it does not merely confirm the baked guid but replaces it, since
        /// <c>GuidManager.Add</c> refuses the duplicate and <c>CreateGuid</c> mints a fresh one. The
        /// dictionary is then keyed by a string the actor no longer reports.
        ///
        /// The symptom is a spawned creature that is claimed - claiming reads <c>GuidString</c> and agrees
        /// with itself - and then cannot be found again by anything that resolves it, so it is never
        /// broadcast, never pinned, and released as a body the host has lost. From the other machine that
        /// is a creature only the host can see. It bites the second creature of a group and not the
        /// first, which is what makes it look intermittent.
        /// </summary>
        private static bool TryResolveOnHost(EntityGameHandler entities, EnemyNetId id, out Actor actor)
        {
            if (_spawnedBodies.TryGetValue(id, out Actor? spawned))
            {
                actor = spawned!;
                return actor != null;
            }

            actor = null!;

            if (!_guidsById.TryGetValue(id, out string? guid)) return false;

            actor = entities.GetActorFromGuid(guid);
            return actor != null;
        }

        /// <summary>
        /// Records that a creature is dead, so a body streaming in later comes up as a corpse.
        /// </summary>
        /// <param name="guid">Which creature.</param>
        internal static void RecordDead(string guid)
        {
            if (!string.IsNullOrEmpty(guid))
            {
                _graveyard.Add(guid);

                // A death outranks an earlier "alive". IsKnownAlive already checks the graveyard, but the
                // word set also feeds HasAnyAlive, and a stale entry there keeps the save-health hook
                // reaching for guids long after the last creature it could apply to.
                _aliveByWord.Remove(guid);
            }
        }

        /// <summary>Whether the host has said this creature is dead.</summary>
        internal static bool IsKnownDead(string guid) => _graveyard.Contains(guid);

        /// <summary>
        /// Calls a creature alive by decree, overruling an earlier death. This is the revive command's
        /// write, and the one place where the graveyard does not win: the point is to take a recorded
        /// death back. The caller vouches for the guid being authored, not runtime-minted.
        /// </summary>
        /// <param name="guid">Which creature.</param>
        internal static void ForceAlive(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            _graveyard.Remove(guid);
            _aliveByWord.Add(guid);
        }

        /// <summary>
        /// The guid a handle was bound under on this client.
        /// </summary>
        /// <param name="id">The handle.</param>
        /// <param name="guid">The creature's guid.</param>
        /// <returns>False when the handle is not bound here.</returns>
        internal static bool TryGetBoundGuid(EnemyNetId id, out string guid)
        {
            return _boundGuids.TryGetValue(id, out guid!);
        }

        /// <summary>
        /// The guid a claimed creature is known by, when that guid means the same thing on every machine.
        ///
        /// Refused for anything spawned at runtime: its guid was minted here and names it only here, so
        /// saying it out loud tells the other machine about a creature it has never heard of.
        /// </summary>
        /// <param name="id">The handle.</param>
        /// <param name="guid">The creature's guid.</param>
        /// <returns>False when the handle is unknown or names a runtime creature.</returns>
        internal static bool TryGetSharedGuid(EnemyNetId id, out string guid)
        {
            guid = string.Empty;

            if (_spawnedClaims.Contains(id)) return false;

            return _guidsById.TryGetValue(id, out guid!) && !string.IsNullOrEmpty(guid);
        }

        /// <summary>
        /// Whether the host has this creature alive and has not since said it is dead - either because it
        /// has claimed a body for it, or because it answered "alive" from its save for a corpse it is
        /// nowhere near.
        ///
        /// Asked by guid because the two things that need it - the save load and the streaming system - meet
        /// a body long before anything knows which handle it belongs to.
        /// </summary>
        /// <param name="guid">The creature's authored guid.</param>
        internal static bool IsKnownAlive(string guid)
        {
            return (_boundByGuid.Contains(guid) || _aliveByWord.Contains(guid)) && !_graveyard.Contains(guid);
        }

        /// <summary>
        /// Whether the host has called anything alive at all. One integer read, so that the hooks which
        /// run for every body in the world can rule themselves out before touching a guid.
        /// </summary>
        internal static bool HasAnyAlive => _boundByGuid.Count > 0 || _aliveByWord.Count > 0;

        /// <summary>How many creatures are alive only by the host's word. For <c>coop_status</c>.</summary>
        internal static int AliveByWord => _aliveByWord.Count;

        /// <summary>
        /// Whether this machine holds a session binding for the actor. What is bound is placed by the
        /// host's live stream, and nothing else should place it.
        /// </summary>
        /// <param name="guid">The actor's authored guid.</param>
        internal static bool IsInSessionHere(string guid)
        {
            return !string.IsNullOrEmpty(guid) && _boundByGuid.Contains(guid);
        }

        /// <summary>
        /// Whether an actor is far enough along to be read from or written to.
        ///
        /// The modules are never null, and that is what makes this necessary rather than tidy. Every one
        /// of them - movement, look, actions, the AI - is a <c>[SerializeField]</c> instance on the actor, so
        /// a null check on <c>GetMovementModule()</c> always passes and proves nothing. What is genuinely
        /// absent is the state they build in <c>StartModule</c>: <c>MovementModule.DirectionModule</c> is
        /// constructed there, and both reading it and calling <c>SetDirection</c> dereference it.
        ///
        /// That window is wide and ordinary, not a race. <c>LazyActor.LoadActor</c> registers the body in the
        /// handler's guid table and only then awaits <c>Actor.StartInitAsync</c>, which starts the modules one
        /// at a time and yields whenever the game's loading budget runs out - so a creature is resolvable by
        /// guid, and therefore claimable, describable and drivable, for several frames before its movement
        /// can answer a question. <c>IsInitialized</c> is the game's own flag for the end of that.
        /// </summary>
        /// <param name="actor">The body.</param>
        internal static bool IsReady(Actor actor)
        {
            return actor != null && !actor.IsDestroyed && actor.IsInitialized;
        }

        /// <summary>
        /// Everything the host knows to be dead, for sending to somebody who has just joined.
        /// </summary>
        internal static IReadOnlyCollection<string> Graveyard => _graveyard;

        /// <summary>
        /// Walks the world's own index of actors and decides what to claim and what to give back.
        ///
        /// Reads <c>EntityGameHandler</c>'s guid dictionary directly. It is the index the game resolves
        /// guids through, so what it holds is exactly the set of creatures a reported handle will be able
        /// to find - which makes it the right thing to iterate rather than a convenient one.
        /// </summary>
        private static void Sweep()
        {
            try
            {
                if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

                Il2CppSystem.Collections.Generic.Dictionary<string, Actor> byGuid = entities._entityGuidDictionary;
                if (byGuid == null) return;

                if (!PlayerAccess.TryGetPlayer(out _) && RemotePlayers.Positions.Count == 0) return;

                RebindForNewcomers(entities);

                foreach (Il2CppSystem.Collections.Generic.KeyValuePair<string, Actor> pair in byGuid)
                {
                    Consider(pair.Key, pair.Value);
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
        /// Tells somebody who has just arrived about every creature the host already owns.
        ///
        /// A claim is an event, and somebody who was not listening never hears it. A bind is sent once,
        /// at the moment a creature is claimed, and <see cref="Consider"/> returns early forever afterwards
        /// because the creature is already claimed. A session is started from a world that has been running,
        /// so by the time anybody joins the host has already claimed everything around itself - and every one
        /// of those claims was announced to nobody.
        ///
        /// What that produces is the failure that looks least like one: the host owns a creature, keeps
        /// broadcasting its position and health, and the client has never been told which of its own
        /// creatures any of it refers to. So it applies none of it. Health does not cross, death does not
        /// cross, and a corpse the client's own save left standing is never handed to
        /// <see cref="EnemyRebirth"/>, because that only runs for creatures that are bound. Which creatures
        /// happen to work is then decided by whether the host claimed them before or after the join, which
        /// is why it looks arbitrary.
        ///
        /// Re-announced wholesale rather than tracked per listener, for the reason
        /// <see cref="SpawnedEnemies.AnnounceToNewcomers"/> gives: the far side keeps the newest bind for a
        /// handle, so saying it twice costs a packet and nothing else.
        /// </summary>
        private static void RebindForNewcomers(EntityGameHandler entities)
        {
            int roster = CoopSession.Players.Count;

            if (roster != _lastRosterCount)
            {
                _lastRosterCount = roster;

                // Repeated, because the first one is sent into a window the far side cannot receive in.
                // A newcomer is registered here the moment their handshake is accepted, and everything their
                // own receive path is handed before their session finishes becoming one is discarded. This
                // happens to work today only because the sweep runs on a quarter-second beat and the first
                // attempt therefore lands late; that is luck rather than design, and it stops being true the
                // moment the sweep gets faster.
                _rebindsLeft = RebindRepeats;
            }

            if (_rebindsLeft <= 0) return;

            _rebindsLeft--;

            if (_claimed.Count == 0) return;

            int sent = 0;

            foreach (EnemyNetId id in _claimed)
            {
                // Spawned creatures are paired by their spawn announcement rather than by a guid, and
                // SpawnedEnemies re-announces those on the same signal. A bind for one would name a guid the
                // far side cannot resolve. Who moves one is restated here though, because a newcomer
                // assumes the host drives whatever it pairs - and ReviewWhoDrives only speaks on change,
                // so an answer that has been "the client's own" since before the join would otherwise
                // never be said to the one machine that missed it.
                if (_spawnedClaims.Contains(id))
                {
                    if (_drives.TryGetValue(id, out bool spawnedDrives))
                    {
                        CoopSession.SendEnemyDrive(id, spawnedDrives);
                    }

                    continue;
                }

                if (!_guidsById.TryGetValue(id, out string? guid)) continue;
                if (!TryResolveOnHost(entities, id, out Actor actor)) continue;

                Health health = actor.GetHealth();
                if (health == null) continue;

                // Health is sent with what is true now rather than what was true when it was claimed,
                // which is what makes this the right repair for a bind that was merely lost. Who drives
                // is the one figure sent as recorded instead: recomputing it here uses the claim range,
                // and a host standing in the band between claim and release ranges would tell every
                // client "not driven" while its own book still says driven - two states the damage
                // forwarding requires to agree, drifted apart by a message whose job is repair. A stale
                // recorded answer is the review's to correct, through the hysteresis, not this path's to
                // contradict.
                if (!_drives.TryGetValue(id, out bool drives))
                {
                    drives = !EnemyEngagements.TryGetLease(id, out _) && CanDrive(actor.transform.position);
                    _drives[id] = drives;
                }

                SendBind(id, guid, health, drives);

                sent++;
            }

            // Only the first of the repeats says so; the rest describe the same creatures.
            if (sent > 0 && _rebindsLeft == RebindRepeats - 1)
            {
                MelonLogger.Msg($"[Enemy] telling the session about {sent} creature(s) already claimed here, " +
                    $"on each of the next {RebindRepeats} sweeps so a machine still joining does not miss them.");
            }
        }

        /// <summary>
        /// Decides whether one creature should be claimed, and claims it.
        /// </summary>
        private static void Consider(string guid, Actor actor)
        {
            if (string.IsNullOrEmpty(guid) || actor == null) return;

            // A body still assembling itself. Not skipped for safety's sake: claiming it would put it into
            // the broadcast this frame, and describing a creature whose movement has not been started yet is
            // what took the whole broadcast down. The sweep runs four times a second and will find it again.
            if (!IsReady(actor)) return;

            // Players and the bodies shown for them are never claimed. Their health belongs to whoever is
            // playing them, and that is settled elsewhere.
            if (RemotePlayers.IsPlayersBody(actor)) return;

            // Only creatures whose guid means the same thing on both machines - the same test the death
            // path makes, and for the same reason.
            if (!IsSharedIdentity(guid)) return;

            if (_idsByGuid.TryGetValue(guid, out EnemyNetId existing) && _claimed.Contains(existing)) return;

            if (!IsNearAnyPlayer(actor.transform.position, ClaimRange)) return;

            if (!TryTakeId(guid, out EnemyNetId id)) return;

            Health health = actor.GetHealth();
            if (health == null) return;

            _claimed.Add(id);

            bool drives = CanDrive(actor.transform.position);
            _drives[id] = drives;

            SendBind(id, guid, health, drives);
        }

        /// <summary>
        /// Binds a claimed creature at the far end with its health as it stands now.
        /// </summary>
        private static void SendBind(EnemyNetId id, string guid, Health health, bool drives)
        {
            CoopSession.BindEnemy(
                id,
                guid,
                (ushort)Math.Clamp(health.CurHealth, 0, ushort.MaxValue),
                (ushort)Math.Clamp(health.MaxHealth, 0, ushort.MaxValue),
                health.IsDead,
                drives);
        }

        /// <summary>
        /// Says again who moves a creature, when the answer has stopped being the one already sent.
        ///
        /// Who drives is a distance, and both ends of it walk around. It is decided once, at the moment
        /// a creature is claimed, and <see cref="Consider"/> returns at its first line forever afterwards -
        /// so a creature claimed while the two players stood together is marked "the host moves this" for
        /// the rest of the session, whatever either of them does next. Release cannot correct it either,
        /// because release is measured from <em>any</em> player and the client standing next to the
        /// creature is what keeps it claimed.
        ///
        /// Both directions of the stale answer are wrong and they are wrong differently. Marked driven when
        /// the host has walked away, the client's own copy has its AI switched off and waits for a host that
        /// is no longer close enough to have an opinion, so it stands still - which is what "the enemies
        /// just move around and never attack me" looks like from the other machine. Marked not driven when
        /// the host has since walked over, both machines run their own AI for one creature whose health only
        /// one of them owns, and the two drift apart while looking as though they are working.
        ///
        /// Sent as an ordinary bind, which is what the far side already knows how to apply: it keeps the
        /// newest bind for a handle, so this is the same repair path a lost bind takes.
        /// </summary>
        private static void ReviewWhoDrives(EntityGameHandler entities)
        {
            if (_claimed.Count == 0) return;

            foreach (EnemyNetId id in _claimed)
            {
                // A spawned creature is paired by its spawn announcement rather than by a guid, so a bind
                // for one would name something the far side cannot resolve. Who moves it still changes
                // with distance exactly like an authored creature's answer does, so that one fact travels
                // on its own message instead of being frozen at "the host's" forever - which is what left
                // a summon cast far from the host standing beside its caster with its behavior switched
                // off and nothing driving it.
                bool spawned = _spawnedClaims.Contains(id);

                if (!_guidsById.TryGetValue(id, out string? guid)) continue;
                if (!TryResolveOnHost(entities, id, out Actor actor)) continue;

                bool told = _drives.TryGetValue(id, out bool last) && last;

                // Given up at a wider range than it is taken on, the same way a claim is. Handing a
                // creature over is not free - the far side switches its own AI on or off around it - and a
                // host walking along the boundary would otherwise hand the same creature back and forth
                // several times a second.
                //
                // A leased creature is never driven whatever the distance says: the client's scripted
                // brawl is the live copy, and the release announced here is what hands that copy its
                // AI back so the fight can happen.
                bool drives = !EnemyEngagements.TryGetLease(id, out _)
                    && CanDrive(actor.transform.position, told ? ReleaseRange : ClaimRange);

                if (_drives.ContainsKey(id) && told == drives) continue;

                _drives[id] = drives;

                // Named rather than counted, because a count cannot tell three creatures crossing the
                // boundary once from one creature crossing it three times - and the second is a creature
                // whose AI is being switched on and off on the far side, which is visible as a stutter.
                MelonLogger.Msg($"[Enemy] {id} is now " +
                    (drives ? "the host's to move." : "the client's own to move again."));

                if (spawned)
                {
                    CoopSession.SendEnemyDrive(id, drives);
                    continue;
                }

                Health health = actor.GetHealth();
                if (health == null) continue;

                SendBind(id, guid, health, drives);
            }

        }

        /// <summary>
        /// Gives back everything the host can no longer speak for.
        /// </summary>
        private static void ReleaseThoseOutOfReach(EntityGameHandler entities)
        {
            _toRelease.Clear();

            foreach (EnemyNetId id in _claimed)
            {
                if (!_guidsById.TryGetValue(id, out string? guid))
                {
                    _toRelease.Add(id);
                    continue;
                }

                // Through the same resolver the broadcasters use, so a spawned creature is looked up by the
                // reference this file holds rather than by a guid the dictionary may no longer be keyed by.
                // Resolving it differently here is how one gets released the moment it is claimed.
                TryResolveOnHost(entities, id, out Actor actor);

                // The host has lost the body. This is not an error - the creature's chunk unloaded here -
                // but it is exactly the moment authority has to go back, because the host can no longer
                // answer for a creature it cannot see.
                if (actor == null)
                {
                    _toRelease.Add(id);
                    CoopSession.ReleaseEnemy(id, EnemyReleaseMessage.ReasonHostLostBody);
                    continue;
                }

                // A spawned creature is never given back for distance, only for being gone. Releasing
                // one is not the reversible thing it is for an authored creature: what pairs the two copies
                // is the spawn announcement, which happened once, so a release ends the pairing for good and
                // the two machines simulate that creature apart for the rest of its life. An authored
                // creature is simply re-claimed by the next sweep.
                if (_spawnedClaims.Contains(id)) continue;

                if (!IsNearAnyPlayer(actor.transform.position, ReleaseRange))
                {
                    _toRelease.Add(id);
                    CoopSession.ReleaseEnemy(id, EnemyReleaseMessage.ReasonOutOfRange);
                }
            }

            foreach (EnemyNetId id in _toRelease)
            {
                _claimed.Remove(id);
                _spawnedClaims.Remove(id);

                // Forgotten with the claim, so that re-claiming the same creature later sends who moves it
                // rather than matching against what a previous claim happened to say.
                _drives.Remove(id);
                _spawnedBodies.Remove(id);
            }
        }

        /// <summary>
        /// Hands out a handle, or refuses once they run out.
        ///
        /// Refuses rather than recycles. Sixty-five thousand creatures in one session is far past any
        /// real play, so reaching the end means something is wrong - and the safe response to that is a
        /// creature nobody synchronizes, not a number that now means two things.
        /// </summary>
        private static bool TryTakeId(string guid, out EnemyNetId id)
        {
            if (_idsByGuid.TryGetValue(guid, out id)) return true;

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
            _idsByGuid[guid] = id;
            _guidsById[id] = guid;
            return true;
        }

        /// <summary>
        /// Whether the host can drive this creature's body as well as own its health.
        ///
        /// Only within the host's own view, and that is a precondition rather than a preference. A
        /// driven creature has its AI switched off at the far end, so if the host's own copy is not being
        /// ticked, the client is left watching a body that has stopped deciding anything on either
        /// machine. Everything that decides whether the host ticks an AI - perception, the distance gates,
        /// the safety check - is satisfied for free while the host's own player is nearby, and is not
        /// satisfied at all when they are elsewhere.
        ///
        /// Widening this to any player's surroundings is what pinning and perception are for, and until
        /// they exist a creature outside the host's view is better left simulating locally: it looks right
        /// to the person standing next to it, which is the only person watching.
        /// </summary>
        /// <param name="position">Where the creature is.</param>
        /// <param name="range">How close counts. The claim range when deciding to take a creature on, the
        /// wider release range when deciding whether to keep one already taken - see
        /// <see cref="ReviewWhoDrives"/>.</param>
        private static bool CanDrive(Vector2 position, float range = ClaimRange)
        {
            if (!PlayerAccess.TryGetPlayer(out Actor local)) return false;

            Vector2 host = local.transform.position;

            return (host - position).sqrMagnitude <= range * range;
        }

        // CanDrive is the host's own player's reach, so this is that or any remote player's.
        private static bool IsNearAnyPlayer(Vector2 position, float range)
        {
            return CanDrive(position, range) || RemotePlayers.IsAnyWithin(position, range);
        }

        /// <summary>
        /// Takes the binds and releases that have arrived and acts on them.
        /// </summary>
        private static void DrainClientMessages()
        {
            while (CoopSession.TryTakeEnemyBind(out EnemyBindMessage bind))
            {
                Bind(bind);
            }

            while (CoopSession.TryTakeEnemyRelease(out EnemyReleaseMessage release))
            {
                Release(release.NetId);
            }

            while (CoopSession.TryTakeEnemyDrive(out EnemyDriveMessage drive))
            {
                ApplyDrive(drive);
            }

            while (CoopSession.TryTakeGraveyard(out EnemyGraveyardMessage chunk))
            {
                foreach (string guid in chunk.Guids)
                {
                    RecordDead(guid);

                    // The host has answered, so this guid stops being asked about - and, since the answer
                    // was "dead", the body is laid out as soon as one exists to lay out.
                    HostWorldState.RecordAnswered(guid);
                    RemoteEnemyHealth.QueueKnownDeath(guid);
                }
            }

            while (CoopSession.TryTakeAlive(out EnemyAliveMessage alive))
            {
                foreach (string guid in alive.Guids)
                {
                    RecordAlive(guid);
                }
            }

            while (CoopSession.TryTakeRevive(out EnemyReviveMessage revive))
            {
                foreach (string guid in revive.Guids)
                {
                    Revive(guid);
                }
            }
        }

        /// <summary>
        /// Takes in the host's decree that a creature is alive again, whatever was said before.
        ///
        /// Unlike <see cref="RecordAlive"/> the graveyard does not win here. A revive is the host
        /// deliberately taking a recorded death back, so the death it overrules is the point.
        /// </summary>
        private static void Revive(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            // The guard RecordAlive has, for the same reason. A guid minted on this machine names a
            // different creature everywhere else, so a decree about it is noise.
            if (ActorSpawnAccess.IsRuntimeCreated(guid)) return;

            ForceAlive(guid);
            CoopSession.ForgetEnemyDeath(guid);
            HostWorldState.RecordAnswered(guid);
            EnemyRebirth.AllowAgain(guid);
            EnemyRebirth.Request(guid);
        }

        /// <summary>
        /// Takes in the host's word that a creature this world has as a corpse is alive.
        ///
        /// The answer that used to be unsayable. It closes the question, refuses this machine's save its
        /// kill from now on - the same overrule a bind buys, through <see cref="IsKnownAlive"/> - and asks
        /// for the corpse already standing here to be rebuilt. The creature that results is an ordinary
        /// local one: unclaimed, running its own behavior, its health its own, until the host comes close
        /// enough to claim it like anything else.
        /// </summary>
        private static void RecordAlive(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;

            // A death already recorded wins. The graveyard is fed by death broadcasts as well as by
            // answers, so an alive answer that crosses paths with a fresher death must not stand a
            // creature up that the session just laid out.
            if (_graveyard.Contains(guid)) return;

            // A guid minted on this machine names nothing in the host's save, and the game reads "no
            // record" as alive - so for these the host's word is an artifact of the question, not
            // knowledge. Nothing should ever ask about one, but the answer is where the damage would
            // happen, so it is also where the guard lives.
            if (ActorSpawnAccess.IsRuntimeCreated(guid)) return;

            _aliveByWord.Add(guid);
            HostWorldState.RecordAnswered(guid);
            EnemyRebirth.Request(guid);
        }

        /// <summary>
        /// Collects every creature this world's save already holds as dead.
        ///
        /// The session graveyard only knows deaths that happened while a session ran. A camp the host
        /// cleared before any session existed is dead in the save alone, and without this a joining
        /// player watched those creatures stand until the host streamed the corpses in and the death
        /// watch re-reported them. Read from the same lazy-actor dictionary <c>coop_revive</c> walks,
        /// which answers without the creature being loaded.
        /// </summary>
        /// <param name="into">Where the guids go.</param>
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

                // A runtime-minted guid names nothing in another machine's world.
                if (ActorSpawnAccess.IsRuntimeCreated(guid)) continue;

                // A revive this session decreed outranks what the save still says.
                if (IsKnownAlive(guid)) continue;

                if (!lazy.IsDead()) continue;

                into.Add(guid);
            }
        }

        private static void Bind(in EnemyBindMessage bind)
        {
            if (string.IsNullOrEmpty(bind.ActorGuid)) return;

            _boundGuids[bind.NetId] = bind.ActorGuid;
            _boundByGuid.Add(bind.ActorGuid);

            // The host claiming a creature is an answer about it, whichever way the answer goes: it has a
            // body for it, so it knows whether it is standing. Recorded so the asking stops - without this,
            // a creature the host has alive is asked about every five seconds for the rest of the session,
            // because nothing else ever calls the question closed.
            HostWorldState.RecordAnswered(bind.ActorGuid);

            SetDriven(bind.NetId, bind.HostDrives);

            if (bind.IsDeadAtBind)
            {
                RecordDead(bind.ActorGuid);

                // And laid out, not merely written down. Recording is bookkeeping; laying the body out is
                // what turns a known death into a corpse on the ground, and that only happens when a
                // bound creature's body is first seen or replaced - which a rebind of an already-bound
                // creature is not. That is how the last chance to repair a divergence gets received and
                // discarded: the handoff says "this one is dead", the book agrees, and the creature goes
                // on standing there. Resolving the guid and skipping a body that is already dead is the
                // queue's own job.
                RemoteEnemyHealth.QueueKnownDeath(bind.ActorGuid);
            }
        }

        /// <summary>
        /// Whether the host drives this creature's body, not merely its health.
        ///
        /// A creature the host owns but does not drive keeps running its own behavior here, which is the
        /// right answer when the host is nowhere near it: it looks correct to the only player watching,
        /// and only its health is arbitrated.
        /// </summary>
        internal static bool IsDriven(EnemyNetId id) => _driven.Contains(id);

        /// <summary>
        /// Whether the host is the machine moving this claimed creature's body, asked on the host.
        ///
        /// What the damage watch needs, and a different question from claimed. A claimed creature the
        /// host does not drive is simulated independently at both ends: the far machine's own copy is
        /// already hitting its own player with its own numbers, so a blow this machine's copy lands on a
        /// puppet must not also cross the wire - that is the same blow billed twice.
        /// </summary>
        internal static bool DrivesOnHost(EnemyNetId id)
        {
            return _drives.TryGetValue(id, out bool drives) && drives;
        }

        /// <summary>
        /// Takes in who moves a spawned creature.
        ///
        /// Remembered even when the handle is not bound yet. The statement, the claim and the pairing
        /// leave the host together, but a summon pairing can wait seconds in <see cref="SummonWatch"/>
        /// for its body - and a statement applied only to bound creatures would be lost exactly when it
        /// matters most, at the birth of a summon the host is nowhere near.
        /// </summary>
        private static void ApplyDrive(in EnemyDriveMessage drive)
        {
            _driveStated[drive.NetId] = drive.HostDrives;

            // Only a creature this client actually shows changes state now; the rest consult the
            // statement when they are paired. RemoteEnemies reconciles the driven set every frame, so
            // removal here is what hands a copy back to its own behavior.
            if (!_boundGuids.ContainsKey(drive.NetId)) return;

            SetDriven(drive.NetId, drive.HostDrives);
        }

        private static void SetDriven(EnemyNetId id, bool driven)
        {
            if (driven)
            {
                _driven.Add(id);
            }
            else
            {
                _driven.Remove(id);
            }
        }

        private static void Release(EnemyNetId id)
        {
            if (_boundBodies.TryGetValue(id, out Actor? actor) && actor != null)
            {
                Restore(id, actor);
            }

            if (_boundGuids.TryGetValue(id, out string? guid))
            {
                _boundByGuid.Remove(guid);
            }

            _boundGuids.Remove(id);
            _boundBodies.Remove(id);
            _spawnedBound.Remove(id);
            _driven.Remove(id);
            _driveStated.Remove(id);
            _protectedByUs.Remove(id);
        }

        /// <summary>
        /// Keeps the client's picture of bound creatures in step with what actually exists.
        ///
        /// Reconciled every frame from state rather than driven by events, for the reason the puppets are:
        /// a body can appear or be destroyed without anything telling this mod, and comparing what is to
        /// what should be covers both without either needing to know about the other.
        /// </summary>
        private static void ReconcileBoundBodies()
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return;

            foreach (KeyValuePair<EnemyNetId, string> bound in _boundGuids)
            {
                // A body this machine built itself is kept by reference and never looked up again. The
                // guid it was recorded under is not one the handler can be trusted to resolve - see
                // TryResolveOnHost - so re-resolving it here returns nothing, the entry is dropped, and the
                // creature is dropped again the next frame and every frame after. What that looks like is a
                // creature standing where it was created while the far side's copy walks away.
                if (_spawnedBound.Contains(bound.Key))
                {
                    if (_boundBodies.TryGetValue(bound.Key, out Actor? built) && built != null) continue;

                    _boundBodies.Remove(bound.Key);
                    continue;
                }

                Actor actor = entities.GetActorFromGuid(bound.Value);

                if (actor == null)
                {
                    // The body went away under us - its chunk unloaded here. The binding stands, because
                    // the host still owns the creature; there is simply nothing to apply it to yet.
                    _boundBodies.Remove(bound.Key);
                    continue;
                }

                if (_boundBodies.TryGetValue(bound.Key, out Actor? known) && known != null && known.Pointer == actor.Pointer)
                {
                    Bare(bound.Key, actor);
                    continue;
                }

                _boundBodies[bound.Key] = actor;

                Protect(bound.Key, actor);
                Bare(bound.Key, actor);
            }
        }

        /// <summary>
        /// Takes the write-protection off a bound creature while this player's scripted brawl runs
        /// against it, and puts it back when the fight is over.
        ///
        /// The brawl was installed on this machine's copy, so this machine's blows have to land on it
        /// for the fight to progress and resolve - which is what sets the quest's own outcome
        /// variables. Everything stays local: the host holds its copy and does not drive or speak for
        /// the creature until the engagement ends.
        /// </summary>
        private static void Bare(EnemyNetId id, Actor actor)
        {
            try
            {
                bool leased = EngageWatch.IsBrawling(id);

                if (leased && _protectedByUs.Contains(id))
                {
                    Restore(id, actor);
                    _leaseBared.Add(id);
                    return;
                }

                if (!leased && _leaseBared.Remove(id))
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
        /// Stops this machine deciding a bound creature's health.
        ///
        /// Invincibility rather than a patch or a blocker. The flag is read inside the health
        /// calculation itself, so it holds even where the method around it has been inlined - which a
        /// prefix does not. And it suppresses only the write: the full figure is still computed, the
        /// damaged event still fires, aggro is still tagged and the blood still plays, so the client's own
        /// blows look and feel exactly as they did while the number they produce is discarded in favor of
        /// the host's.
        /// </summary>
        private static void Protect(EnemyNetId id, Actor actor)
        {
            try
            {
                Health health = actor.GetHealth();
                if (health == null) return;

                // Recorded only when this mod is the one turning it on. A creature the game had already
                // made invulnerable must not be handed back killable when the session ends.
                if (!health.IsInvincible)
                {
                    _protectedByUs.Add(id);
                    health.SetInvinciblity(true);
                }

                if (IsKnownDead(_boundGuids[id]))
                {
                    if (!health.IsDead)
                    {
                        RemoteEnemyHealth.QueueKnownDeath(_boundGuids[id]);
                    }

                    return;
                }

                // The host says this creature is alive and this machine has it lying dead. The body cannot be
                // corrected in place - see EnemyRebirth for why the death is not one thing to undo - so it is
                // rebuilt, which is the streaming system's own job and produces a body with nothing wrong
                // with it.
                if (health.IsDead)
                {
                    EnemyRebirth.Request(_boundGuids[id]);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] write-protecting a bound creature failed: " + e);
            }
        }

        private static void Restore(EnemyNetId id, Actor actor)
        {
            if (!_protectedByUs.Remove(id)) return;

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
            // Walks the bodies rather than the protected set, because Restore removes from that set.
            foreach (KeyValuePair<EnemyNetId, Actor> bound in _boundBodies)
            {
                if (bound.Value != null)
                {
                    Restore(bound.Key, bound.Value);
                }
            }

            _protectedByUs.Clear();
        }

        /// <summary>
        /// Claims a creature that was spawned at runtime here.
        ///
        /// Keyed by the creature's own local guid, which is meaningless to anybody else but is exactly
        /// what this machine resolves bodies with. The far side never sees it - the handle is what
        /// crosses, and the far side keeps its own body against that handle.
        /// </summary>
        /// <param name="actor">The creature.</param>
        /// <param name="id">The handle it will be known by.</param>
        /// <returns>False when it has no usable local identity or handles have run out.</returns>
        internal static bool TryClaimSpawned(Actor actor, out EnemyNetId id)
        {
            id = EnemyNetId.None;

            string guid = actor.GetGuidComponent()?.GuidString ?? string.Empty;
            if (string.IsNullOrEmpty(guid)) return false;

            if (!TryTakeId(guid, out id)) return false;

            _claimed.Add(id);
            _spawnedClaims.Add(id);
            _spawnedBodies[id] = actor;

            // Claimed but not bound. A bind names a guid both machines read out of the same scene, and a
            // spawned creature's copies each have their own. What pairs them is the spawn announcement.
            // The claim is still needed, because ClaimedIds is the set every broadcaster walks.

            // Who moves it is decided the way it is for an authored creature - by where the host is - and
            // said at once rather than left to the review, because the review only speaks on change. A
            // summon cast by a player standing next to the host starts driven; one cast across the map
            // starts as its caster's machine's own, which is the only machine watching it fight. The
            // statement leaves before the spawn or summon announcement does, on the same ordered channel,
            // so it can never arrive after a pairing that would default it.
            bool drives = CanDrive(actor.transform.position);
            _drives[id] = drives;

            CoopSession.SendEnemyDrive(id, drives);

            return true;
        }

        /// <summary>
        /// Records a body this machine built because the host announced a spawn.
        ///
        /// Bound from the moment it exists, and driven only when the host says so. The claim itself
        /// sends who moves it before the announcement that pairs it, so by the time a body reaches here
        /// the statement has normally already arrived - and when the host is across the map, its answer
        /// is "not me", which leaves this copy fighting under its own behavior on the one screen that is
        /// watching it. Driven is still the default for a statement that went missing, because a summon
        /// beside the host that briefly runs its own AI is corrected by the next review, while one left
        /// permanently undriven is two creatures wearing one handle.
        /// </summary>
        /// <param name="id">The host's handle.</param>
        /// <param name="body">The body built here.</param>
        internal static void BindSpawnedBody(EnemyNetId id, Actor body)
        {
            if (body == null) return;

            string guid = body.GetGuidComponent()?.GuidString ?? string.Empty;
            if (string.IsNullOrEmpty(guid)) return;

            _boundGuids[id] = guid;
            _boundByGuid.Add(guid);
            _boundBodies[id] = body;
            _spawnedBound.Add(id);

            SetDriven(id, !_driveStated.TryGetValue(id, out bool stated) || stated);

            Protect(id, body);
        }

        /// <summary>
        /// Whether this guid names the same creature on every machine. See
        /// <see cref="EnemyDeathWatch"/>, which asks the same question for the same reason and explains why
        /// the lazy-actor table on its own is not the answer.
        ///
        /// Getting this wrong costs more here than there. A death sent under a guid nobody can resolve
        /// is one creature the far side never lays down. A claim sent under one takes a handle, pins the
        /// body on this machine for the rest of the session, and produces a binding the far side can never
        /// attach to a body - so it also keeps broadcasting positions for a creature that does not exist
        /// over there.
        /// </summary>
        internal static bool IsSharedIdentity(string guid)
        {
            if (!EntityGameHandler.TryGet(out EntityGameHandler entities) || entities == null) return false;

            // Spawned by a mod through the API, so the guid means nothing anywhere else even though the
            // game's own table holds a handle for it. Shared through SpawnedEnemies instead, which
            // replicates what the creature was built from rather than what it is called.
            if (ActorSpawnAccess.IsRuntimeCreated(guid)) return false;

            return entities.GetLazyActorFromGuid(guid) != null;
        }
    }
}
