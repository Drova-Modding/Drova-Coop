using System.Diagnostics.CodeAnalysis;
using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Coop;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Enemies;
using DrovaCoopMod.Systems.Loot;
using DrovaCoopMod.Systems.Players;
using DrovaCoopMod.Systems.Session;
using DrovaCoopMod.Systems.World;
using Il2CppDrova;
using MelonLoader;

namespace DrovaCoopMod.Access
{
    /// <summary>
    /// Answers the API's <c>CoopAccess</c> questions for this mod, so other mods can ask without referencing it.
    /// </summary>
    internal sealed class CoopProvider : ICoopProvider
    {
        /// <summary>The one instance, so registering and unregistering match by reference.</summary>
        private static readonly CoopProvider Instance = new();

        private CoopProvider()
        {
        }

        /// <summary>Registers this mod as the API's co-op provider.</summary>
        internal static void Register()
        {
            CoopAccess.RegisterProvider(Instance);
        }

        /// <summary>Restores the single-player answers when the melon shuts down.</summary>
        internal static void Unregister()
        {
            CoopAccess.UnregisterProvider(Instance);
        }

        /// <summary>True only with another player present, not merely an open session.</summary>
        public bool IsMultiplayer
        {
            get
            {
                if (!CoopSession.IsLive)
                {
                    return false;
                }

                return CoopSession.Players.Count > 1;
            }
        }

        /// <summary>True unless this machine is a joined client. A client still connecting owns its own world.</summary>
        public bool IsWorldAuthority => CoopSession.State != CoopSessionState.Joined;

        /// <summary>A seed every peer agrees on, drawn by the host per session (relay or direct). Not stable across sessions.</summary>
        public ulong SessionSeed => CoopSession.SessionSeed;

        /// <inheritdoc/>
        public void GetPlayers(List<CoopPlayer> buffer)
        {
            buffer.Clear();

            foreach (KeyValuePair<PlayerId, string> player in CoopSession.Players)
            {
                bool isLocal = player.Key == CoopSession.LocalPlayerId;

                // Another player's level is not on the wire, so it stays 0 ("not known").
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
        public bool TryGetPlayerActor(CoopPlayerId id, [NotNullWhen(true)] out Actor? actor)
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

            actor = null;
            return false;
        }

        /// <inheritdoc/>
        public bool IsRemotePlayer(Entity? entity)
        {
            return entity != null && RemotePlayers.IsPuppet(entity);
        }

        /// <summary>True while any replication scope is writing to the game.</summary>
        public bool IsReplicating =>
            RemoteApply<HealthScope>.InProgress
            || RemoteApply<EnemyHealthScope>.InProgress
            || RemoteApply<EnemyBlowScope>.InProgress
            || RemoteApply<HaulScope>.InProgress
            || RemoteApply<WorkScope>.InProgress
            || RemoteApply<GVarScope>.InProgress
            || RemoteApply<ItemUseScope>.InProgress
            || RemoteApply<KnowledgeScope>.InProgress
            || RemoteApply<StatusScope>.InProgress;

        /// <summary>What this mod actually keeps in step. A false yes would cause the divergence a mod asks to avoid.</summary>
        public bool Supports(CoopCapability capability)
        {
            return capability switch
            {
                CoopCapability.PlayerBodies => true,
                CoopCapability.PlayerHealth => true,
                CoopCapability.StatusEffects => true,
                CoopCapability.Knowledge => true,
                CoopCapability.Experience => true,

                // Kind, position, health and death match. Rolled equipment or names roll separately at each end.
                CoopCapability.SpawnedNpcs => true,

                // Death crosses, the health number does not.
                CoopCapability.EnemyHealth => false,

                // The scopes exist and nothing uses them yet.
                CoopCapability.GlobalVariables => false,

                // The host's loot rule, adopted by every client.
                CoopCapability.Loot => CoopSession.SharesWorld && CoopSession.LootMode == CoopProtocol.LootModeShared,

                _ => false,
            };
        }

        /// <summary>Tells the API the session changed, so mods drop cached answers.</summary>
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
