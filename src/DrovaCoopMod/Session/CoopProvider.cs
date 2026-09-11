using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Coop;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.World;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Session
{
    /// <summary>
    /// Answers the API's co-op questions on behalf of this mod, so other mods can behave correctly in a
    /// shared world without depending on this one.
    ///
    /// The direction of the dependency is the whole point. The API ships to everybody and cannot
    /// reference a co-op mod; this mod already references the API. So a mod that wants to know whether it
    /// should spawn a bear asks <c>CoopAccess</c>, which asks whatever registered - and on a machine with
    /// no co-op mod installed, that is a stub answering "you are alone and you decide", which is exactly
    /// what makes the check safe to write in the first place.
    ///
    /// Everything here is a read of state this session already keeps. Nothing new is tracked for it.
    /// </summary>
    internal sealed class CoopProvider : ICoopProvider
    {
        /// <summary>The one instance, so registering and unregistering can match by reference.</summary>
        internal static readonly CoopProvider Instance = new();

        private CoopProvider()
        {
        }

        /// <summary>
        /// Registers this mod as the answer to the API's co-op questions. Called once at melon init.
        /// </summary>
        internal static void Register()
        {
            CoopAccess.RegisterProvider(Instance);
        }

        /// <summary>
        /// Hands the questions back to their single-player answers. Called when the melon shuts down, so a
        /// mod asking after this point is told it is alone rather than being told about a dead session.
        /// </summary>
        internal static void Unregister()
        {
            CoopAccess.UnregisterProvider(Instance);
        }

        /// <summary>
        /// Somebody else, not merely a socket. A host sitting in a session nobody has joined is not
        /// playing together, and a mod that stopped spawning things the moment a session opened would be
        /// disabled for a player waiting alone in a lobby.
        /// </summary>
        public bool IsMultiplayer
        {
            get
            {
                if (CoopSession.State != CoopSessionState.Hosting && CoopSession.State != CoopSessionState.Joined)
                {
                    return false;
                }

                return CoopSession.Players.Count > 1;
            }
        }

        /// <summary>
        /// True unless this machine is a client, which is the same rule the rest of the mod runs on:
        /// the world is host-authoritative, and outside a session the local machine is the only one there
        /// is. A client that is still connecting is not yet a client for this purpose - it owns its own
        /// world until the session is real - so the state is what decides, not the transport role.
        /// </summary>
        public bool IsWorldAuthority => CoopSession.State != CoopSessionState.Joined;

        /// <summary>
        /// A number both machines agree on.
        ///
        /// Derived from the session code, which is the one thing every peer in a session has seen and
        /// which differs between sessions. Not stable across a save and load, and it cannot be: a
        /// Drova save is identified by hero and slot index alone, which are the same numbers on every
        /// machine and in every playthrough.
        /// </summary>
        public ulong SessionSeed
        {
            get
            {
                string? code = CoopSession.SessionCode;
                if (string.IsNullOrEmpty(code)) return 0;

                // FNV-1a rather than string.GetHashCode, which .NET randomizes per process - it would give
                // each machine a different answer and fail the one job this has.
                ulong hash = 14695981039346656037UL;

                foreach (char character in code)
                {
                    hash = (hash ^ character) * 1099511628211UL;
                }

                return hash;
            }
        }

        /// <inheritdoc/>
        public void GetPlayers(List<CoopPlayer> buffer)
        {
            buffer.Clear();

            foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
            {
                bool isLocal = player.Key == CoopSession.LocalPlayerId;

                // Only for the local player. Another player's level is not on the wire, and a guess would
                // be worse than the zero that means "not known" - a mod scaling by it would scale by
                // whatever this machine happened to invent.
                int level = 0;
                if (isLocal)
                {
                    PlayerAccess.TryGetLevel(out level);
                }

                buffer.Add(new CoopPlayer(
                    new CoopPlayerId(player.Key.Value),
                    player.Value,
                    isLocal,
                    isLocal ? IsWorldAuthority : !IsWorldAuthority,
                    level));
            }
        }

        /// <inheritdoc/>
        public bool TryGetPlayerActor(CoopPlayerId id, out Actor actor)
        {
            if (id.Value == CoopSession.LocalPlayerId.Value && PlayerAccess.TryGetPlayer(out actor))
            {
                return true;
            }

            foreach (KeyValuePair<PlayerId, RemotePlayer> body in RemotePlayers.Bodies)
            {
                if (body.Key.Value != id.Value) continue;

                actor = body.Value.Body;
                return actor != null;
            }

            actor = null!;
            return false;
        }

        /// <inheritdoc/>
        public bool IsRemotePlayer(Entity? entity)
        {
            return entity != null && RemotePlayers.IsPuppet(entity);
        }

        /// <summary>
        /// Every scope, not one. A mod asking this wants to know whether the game is being written to
        /// from the wire at all; which subsystem is this mod's own business, and a mod that had to ask per
        /// subsystem would have to be updated every time one was added.
        /// </summary>
        public bool IsReplicating =>
            RemoteApply<HealthScope>.InProgress
            || RemoteApply<EnemyHealthScope>.InProgress
            || RemoteApply<EnemyBlowScope>.InProgress
            || RemoteApply<HaulScope>.InProgress
            || RemoteApply<WorkScope>.InProgress
            || RemoteApply<InventoryScope>.InProgress
            || RemoteApply<GVarScope>.InProgress
            || RemoteApply<ItemUseScope>.InProgress
            || RemoteApply<KnowledgeScope>.InProgress
            || RemoteApply<StatusScope>.InProgress;

        /// <summary>
        /// What this mod actually keeps in step today.
        ///
        /// Answered honestly, including the noes. A mod that asks whether spawned creatures are shared
        /// and is told yes when they are not will produce exactly the divergence it was trying to avoid,
        /// so every entry here has to be moved deliberately when the thing it names starts working.
        /// </summary>
        public bool Supports(CoopCapability capability)
        {
            return capability switch
            {
                CoopCapability.PlayerBodies => true,
                CoopCapability.PlayerHealth => true,
                CoopCapability.StatusEffects => true,
                CoopCapability.Knowledge => true,
                CoopCapability.Experience => true,

                // Announced as a spawn and rebuilt from the same asset at the far end, which covers both the
                // direct and the lazy path. Yes with a limit worth knowing: what matches is the kind of
                // creature, where it stands, its health and when it dies. Anything decided while building it
                // - a rolled set of equipment, a randomized name - is rolled separately at each end.
                CoopCapability.SpawnedNpcs => true,

                // Death crosses; health does not. Reported as unsupported because a mod asking this wants
                // to know whether it can rely on the number, and it cannot.
                CoopCapability.EnemyHealth => false,

                // The scopes exist and nothing uses them yet.
                CoopCapability.GlobalVariables => false,

                CoopCapability.Loot => false,
                _ => false,
            };
        }

        /// <summary>
        /// Tells the API that the session changed, so mods holding a cached answer can drop it.
        /// </summary>
        internal static void NotifyChanged()
        {
            try
            {
                CoopAccess.NotifySessionChanged();
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Coop] telling the API the session changed failed: " + e);
            }
        }
    }
}
