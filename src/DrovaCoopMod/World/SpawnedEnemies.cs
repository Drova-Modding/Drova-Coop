using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.Utilities.LazyLoading;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Makes creatures that mods put into the world at runtime exist for everybody.
    ///
    /// These are the one kind of creature the rest of the design cannot reach. Everything else is named
    /// by the guid its scene was authored with, which both machines read out of the same asset. A creature
    /// instantiated at runtime has a guid minted in the process that made it, so no other machine can be
    /// told which creature is meant - and until now the honest outcome was that it simply did not exist for
    /// the other player. A camp one person was fighting was an empty field to the other.
    ///
    /// The way through is to replicate the spawn rather than the creature: the asset means the same thing
    /// on both machines, so the far side builds its own from the same recipe and the host's handle binds
    /// the two together. After that it is an ordinary member of the session.
    ///
    /// Two machines building from one recipe do not get identical creatures, and that limit is worth
    /// stating rather than hiding. Anything decided while building - a randomised name, a rolled set of
    /// equipment - is rolled separately at each end. What matches is the kind of creature, where it stands,
    /// its health and when it dies, because those are the things that travel afterwards.
    /// </summary>
    internal static class SpawnedEnemies
    {
        /// <summary>A spawn noticed but not shared yet.</summary>
        private readonly record struct Noticed(Actor? Body, LazyActor? Handle, string AssetGuid);

        /// <summary>
        /// One creature this machine spawned and told the session about.
        ///
        /// The asset is kept alongside the handle because announcing is not a one-off: somebody joining an
        /// hour into a session has to be told about everything already standing, and the only way to say
        /// what a creature is remains the asset it was built from.
        /// </summary>
        private readonly struct Announced(EnemyNetId id, string assetGuid, Actor actor)
        {
            internal EnemyNetId Id { get; } = id;

            internal string AssetGuid { get; } = assetGuid;

            internal Actor Actor { get; } = actor;
        }

        // Host: what has been announced, so a creature is announced once per listener. Client: what has
        // been built here for a handle, so a repeated announcement does not produce a second body.
        private static readonly Dictionary<IntPtr, Announced> _announced = new();
        private static readonly Dictionary<EnemyNetId, Actor> _built = new();

        // Spawns noticed but not yet shared - bodies that exist already, and handles whose bodies may be
        // hours away - with what each was built from. Exactly one of Body and Handle is set. See
        // WatchPending.
        private static readonly Dictionary<IntPtr, Noticed> _pending = new();

        /// <summary>
        /// How many times a newcomer is told about what is already standing here.
        ///
        /// More than one because the first is sent into a window the far side cannot receive in; see
        /// AnnounceToNewcomers. Enough of them to cover a slow load on the joining machine.
        /// </summary>
        private const int AnnounceRepeats = 6;

        /// <summary>How long between those repeats.</summary>
        private const float RepeatSeconds = 1f;

        private static bool _subscribed;
        private static int _lastRosterCount;
        private static int _repeatsLeft;
        private static float _sinceRepeat;

        /// <summary>
        /// How many announced spawns this game could not build.
        ///
        /// With the same mods on both machines this should be zero and a non-zero value means the asset id
        /// itself did not survive the trip. It is only a genuine mod-set difference when the two
        /// installations really do differ.
        /// </summary>
        internal static int Unbuildable { get; private set; }

        /// <summary>How many spawned creatures this machine is sharing or has built. For status.</summary>
        internal static int Count => _announced.Count + _built.Count;

        /// <summary>
        /// How many spawns have been noticed but not shared yet. Non-zero is ordinary - a lazy handle whose
        /// creature nobody has walked near sits here indefinitely - but a count that never falls while
        /// <see cref="Count"/> stays at zero means the announcing half is not running.
        /// </summary>
        internal static int Pending => _pending.Count;

        /// <summary>
        /// Starts listening for spawns. Called once at melon init.
        ///
        /// Subscribes to the API rather than patching anything: a spawn only becomes shareable when the
        /// mod that performed it says what it built from, and there is nowhere else that knowledge exists.
        /// </summary>
        internal static void Initialize()
        {
            if (_subscribed) return;

            ActorSpawnAccess.OnActorSpawned += OnActorSpawned;
            ActorSpawnAccess.OnLazyActorSpawned += OnLazyActorSpawned;
            _subscribed = true;
        }

        /// <summary>
        /// Builds whatever the host has announced. Call once per frame from a loaded world.
        /// </summary>
        internal static void Update(float unscaledDeltaTime)
        {
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                WatchPending();
                AnnounceToNewcomers(unscaledDeltaTime);
                return;
            }

            if (CoopSession.State != CoopSessionState.Joined) return;

            // Nothing a client noticed will ever be shared - only the machine that owns the world announces
            // - so holding them is a list that only grows and a status count that reads as work outstanding.
            // Dropped rather than kept, because by the time this machine is a host again those bodies belong
            // to a session that has ended.
            _pending.Clear();

            while (CoopSession.TryTakeEnemySpawn(out EnemySpawnMessage message))
            {
                Build(message);
            }
        }

        /// <summary>
        /// Tells everyone about creatures that were already standing when they arrived.
        ///
        /// A spawn is an event, and somebody who was not listening never hears it. Everything else in
        /// this design describes a state that keeps being restated - health, positions, what is dead - so a
        /// late arrival catches up on its own. A spawn does not: it happens once, and a player who joins a
        /// minute later would walk into a camp that exists for one of them and not the other.
        ///
        /// Re-announced wholesale rather than tracked per listener. The far side ignores a handle it has
        /// already built, so saying it twice costs a packet and nothing else - and the roster is the same
        /// signal the equipment sync uses to solve the same problem.
        /// </summary>
        private static void AnnounceToNewcomers(float unscaledDeltaTime)
        {
            int roster = CoopSession.Players.Count;

            if (roster != _lastRosterCount)
            {
                _lastRosterCount = roster;

                // Said several times over the next few seconds, not once. The host learns somebody has
                // joined the moment their handshake is accepted, and announcing in that same frame sends
                // this before that player's own session has finished becoming a session - and their
                // receive path drops everything that arrives before it is Joined. The result is a spawn
                // that was announced exactly once, to nobody, for a creature that then exists on one
                // machine for the rest of its life.
                //
                // A repeat costs one packet per spawn and is ignored on arrival, since the far side keeps
                // what it has already built. That is a much better trade than a window nobody can see.
                _repeatsLeft = AnnounceRepeats;
                _sinceRepeat = 0f;
            }

            if (_repeatsLeft <= 0) return;

            _sinceRepeat += unscaledDeltaTime;
            if (_sinceRepeat < RepeatSeconds) return;

            _sinceRepeat = 0f;
            _repeatsLeft--;

            if (_announced.Count == 0) return;

            foreach (KeyValuePair<IntPtr, Announced> announced in _announced)
            {
                Actor actor = announced.Value.Actor;

                // Destroyed or dead since. Death travels on its own channel and the far side will hear
                // about it there; re-announcing a corpse would only ask somebody to build one.
                if (actor == null)
                {
                    _announced.Remove(announced.Key);
                    continue;
                }

                Health health = actor.GetHealth();
                if (health != null && health.IsDead)
                {
                    _announced.Remove(announced.Key);
                    continue;
                }

                CoopSession.BroadcastEnemySpawn(announced.Value.Id, announced.Value.AssetGuid, actor.transform.position);
            }

            // Only the first of the repeats says so. The rest are the same sentence about the same
            // creatures and would read as six separate things having happened.
            if (_repeatsLeft == AnnounceRepeats - 1)
            {
                MelonLogger.Msg($"[Enemy] telling the session about {_announced.Count} creature(s) already " +
                    $"standing here, {AnnounceRepeats} times over the next {AnnounceRepeats * RepeatSeconds:0}s " +
                    "so a machine still joining does not miss them.");
            }
        }

        /// <summary>
        /// Forgets everything, and takes away what this machine built on somebody else's word.
        ///
        /// The client's copies go; the host's stay. A creature a mod spawned on the host belongs to
        /// that world and outlives the session. A copy built here exists only because the session said so,
        /// and leaving it behind would put a creature in this player's world that their own game never
        /// created and their save has no business recording.
        /// </summary>
        internal static void Clear()
        {
            foreach (KeyValuePair<EnemyNetId, Actor> built in _built)
            {
                ActorSpawnAccess.TryKill(built.Value);
            }

            _built.Clear();
            _announced.Clear();
            _lastRosterCount = 0;
            _repeatsLeft = 0;
            _sinceRepeat = 0f;

            // The handles and bodies themselves are not this mod's, so they stay; what goes is the claim on
            // them. A creature another mod spawned outlives the session in the world that spawned it.
            _pending.Clear();
        }

        /// <summary>
        /// Notices a lazy handle another mod created, and remembers what its creature is built from.
        ///
        /// The handle is not the creature and cannot be announced as one. A lazy actor is a promise the
        /// streaming system keeps and breaks repeatedly: it builds the body when a player comes near and
        /// destroys it when they leave, so there is nothing to claim at the moment the handle appears and
        /// nothing to send a position for. What is worth keeping is the asset, because the handle is the only
        /// thing that ever knows it.
        /// </summary>
        private static void OnLazyActorSpawned(LazyActor lazyActor, string assetGuid)
        {
            try
            {
                if (lazyActor == null || string.IsNullOrEmpty(assetGuid)) return;

                // Kept whichever end this machine is and whether or not a session is live. A handle created
                // before somebody joins is exactly the case the roster re-announcement exists for, and by
                // the time a session starts the event has long since fired.
                _pending[lazyActor.Pointer] = new Noticed(null, lazyActor, assetGuid);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] noticing a lazy creature another mod created failed: " + e);
            }
        }

        /// <summary>
        /// Shares the spawns that were noticed before there was anybody to share them with, once each has a
        /// body that can answer for itself.
        ///
        /// Only the machine that owns the world announces. A client spawning something of its own is a
        /// mod running where it should not be, and announcing it would put the creature in the host's world
        /// twice.
        ///
        /// A handle is polled from state rather than driven by an event. It builds and destroys its
        /// creature on its own schedule with nothing telling this mod, and polling also avoids holding an
        /// interop delegate alive across scene loads for every creature another mod ever spawned. It is
        /// announced once and then let go of. If its body is rebuilt later, the roster re-announcement is
        /// what covers it.
        /// </summary>
        private static void WatchPending()
        {
            foreach (KeyValuePair<IntPtr, Noticed> pending in _pending)
            {
                (Actor? body, LazyActor? handle, string assetGuid) = pending.Value;

                // Destroyed before anybody could be told, which for a timed event's creature is ordinary.
                // For a handle, the handle itself has gone, so its creature is never coming. A reference
                // test picks the kind, the Unity test below it asks whether it still exists.
                bool gone = handle is null ? body == null : handle == null || handle.IsDestroyed;

                if (gone)
                {
                    _pending.Remove(pending.Key);
                    continue;
                }

                // A handle with no body yet is normal and possibly permanent, since nobody may ever walk over
                // there, so it is kept rather than expired.
                if (handle is not null)
                {
                    body = handle.Actor;
                }

                // Announced from a body that can answer for itself. A creature is claimed by the guid its
                // own components hold, and those are assigned as it initializes.
                if (!EnemyRegistry.IsReady(body!)) continue;

                _pending.Remove(pending.Key);

                if (_announced.ContainsKey(body!.Pointer)) continue;

                Share(body, assetGuid);
            }
        }

        private static void OnActorSpawned(Actor actor, string assetGuid)
        {
            try
            {
                if (actor == null || string.IsNullOrEmpty(assetGuid)) return;

                if (_announced.ContainsKey(actor.Pointer)) return;

                // Remembered whether or not a session is live, and that is the fix for a whole class of
                // missing creature. A session is started from an already-loaded world, so anything spawned
                // in the minutes before somebody joined had its one and only announcement refused - the
                // creature then existed for the host alone for as long as it lived, with nothing to make it
                // appear later. Sharing is what waits for a session; noticing does not.
                _pending[actor.Pointer] = new Noticed(actor, null, assetGuid);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] noticing a spawned creature failed: " + e);
            }
        }

        /// <summary>
        /// Claims one spawned creature and tells the session what it is.
        /// </summary>
        private static void Share(Actor actor, string assetGuid)
        {
            if (!EnemyRegistry.TryClaimSpawned(actor, out EnemyNetId id)) return;

            _announced[actor.Pointer] = new Announced(id, assetGuid, actor);

            Vector2 position = actor.transform.position;

            CoopSession.BroadcastEnemySpawn(id, assetGuid, position);

            MelonLogger.Msg($"[Enemy] a spawned creature is now shared as {id}, built from '{assetGuid}'.");
        }

        private static void Build(in EnemySpawnMessage message)
        {
            try
            {
                if (!message.IsPlausible()) return;
                if (_built.ContainsKey(message.NetId)) return;

                // Built from the asset the announcing machine used, through the API's own recipe - the
                // parenting, the wake-up and the save exclusion are the same ones a local spawn needs.
                if (!ActorSpawnAccess.TrySpawnFromAsset(message.AssetGuid, new Vector2(message.X, message.Y), out Actor actor))
                {
                    Unbuildable++;

                    MelonLogger.Warning($"[Enemy] the other player spawned '{message.AssetGuid}', which this " +
                        "game could not build. Mod sets may differ; that creature will not appear here.");
                    return;
                }

                _built[message.NetId] = actor;

                // Said out loud, once per creature. A spawn that never arrives and one that arrives and
                // cannot be built look identical from here - both are a creature missing on one screen -
                // and only the second leaves a warning, so the first needs a line saying it got this far.
                MelonLogger.Msg($"[Enemy] built {message.NetId} from '{message.AssetGuid}' at " +
                    $"({message.X:0}, {message.Y:0}) because the other player spawned it.");

                // Registered under the host's handle, which is what makes everything else - health,
                // position, death - apply to it exactly as it does to a creature both machines had already.
                EnemyRegistry.BindSpawnedBody(message.NetId, actor);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Enemy] building a creature the other player spawned failed: " + e);
            }
        }
    }
}
