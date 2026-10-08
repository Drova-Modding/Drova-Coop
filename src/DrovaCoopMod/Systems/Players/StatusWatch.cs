using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Sync;
using DrovaCoopMod.Systems.Players.Patches;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.StatusEffects;
using MelonLoader;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// Reports status effects this machine puts on teammates' bodies, so their own game applies them.
    /// Hooks the private <c>AddEffectFromSourceIntern</c>, which both public entry points funnel through.
    /// Bleeding is skipped because it crosses as a flag on the damage report.
    /// </summary>
    internal sealed class StatusWatch : ICoopSystem
    {
        /// <summary>Nothing this machine is entitled to speak for put this effect on.</summary>
        private const byte OriginNone = 0xFF;

        /// <summary>Effects taken off a teammate's body here because they ended on the teammate. For tests.</summary>
        internal static int EndedApplied { get; private set; }

        /// <summary>Effects a teammate's copy tried to put on the player here, refused. For the log and tests.</summary>
        internal static int CopyEffectsRefused { get; private set; }

        void ICoopSystem.Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(StatusEffectHandler), "AddEffectFromSourceIntern",
                typeof(StatusWatchPatches), nameof(StatusWatchPatches.AddEffectPostfix));
            Hooking.TryPrefix(harmony, typeof(StatusEffectHandler), "AddEffectFromSourceIntern",
                typeof(StatusWatchPatches), nameof(StatusWatchPatches.CopyEffectOnLocalPrefix));

            // Every removal ends here: cures, cleanses and expiry.
            Hooking.TryPostfix(harmony, typeof(StatusEffectHandler), nameof(StatusEffectHandler.RemoveEffects),
                typeof(StatusWatchPatches), nameof(StatusWatchPatches.RemoveEffectsPostfix));
        }

        /// <summary>
        /// The local player's effect ended, so their copies elsewhere drop it too.
        /// </summary>
        internal static void RemoveEffectsPostfix(StatusEffectHandler __instance, Il2CppSystem.Collections.Generic.List<StatusEffect>? prefabEffects)
        {
            try
            {
                if (RemoteApply<StatusScope>.InProgress || !CoopSession.IsLive || __instance == null || prefabEffects == null) return;
                if (!RemotePlayers.IsLocalPlayer(__instance._owner)) return;

                foreach (StatusEffect prefab in prefabEffects)
                {
                    if (prefab == null || string.IsNullOrEmpty(prefab.GUID) || __instance.HasStatusEffectApplied(prefab)) continue;
                    ReportStatusEnded(new StatusEndedMessage { Player = CoopSession.LocalPlayerId, EffectId = prefab.GUID });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Status] reporting an ended status effect failed: " + e);
            }
        }

        /// <summary>A teammate's effect ended on them: take it off their body here.</summary>
        private static void TakeEnded(in StatusEndedMessage message)
        {
            try
            {
                if (!RemotePlayers.Bodies.TryGetValue(message.Player, out RemotePlayer? body) || body?.Body == null) return;

                StatusEffectHandler? handler = body.Body.GetStatusEffectHandler();
                SubDatabase_StatusEffect? database = ProviderAccess.StatusEffectDatabase;
                StatusEffect? prefab = database == null ? null : database.GetStatusEffectByGuid(message.EffectId);
                if (handler == null || prefab == null || !handler.HasStatusEffectApplied(prefab)) return;

                using (RemoteApply<StatusScope>.Enter())
                {
                    Il2CppSystem.Collections.Generic.List<StatusEffect> one = new();
                    one.Add(prefab);
                    handler.RemoveAllEffects(one);
                }

                EndedApplied++;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Status] ending a teammate's status effect failed: " + e);
            }
        }

        /// <summary>
        /// Refuses an effect a teammate's copy puts on the local player. The teammate's game reports it, so it would land twice.
        /// </summary>
        internal static bool CopyEffectOnLocalPrefix(StatusEffectHandler __instance, SpawnInfo? info)
        {
            try
            {
                if (RemoteApply<StatusScope>.InProgress) return true;
                if (!CoopSession.IsLive || __instance == null || info == null || !info.HasOwner || info.Owner == null) return true;

                Actor? source = info.Owner.TryCast<Actor>();
                if (source == null || !RemotePlayers.IsPuppet(source)) return true;
                if (!RemotePlayers.IsLocalPlayer(__instance._owner)) return true;

                CopyEffectsRefused++;
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Status] checking a teammate's effect failed: " + e);
                return true;
            }
        }

        /// <summary>
        /// Reports an effect that landed on a remote player's body. Runs for every actor, so guards go cheapest first.
        /// </summary>
        internal static void AddEffectPostfix(StatusEffectHandler __instance, StatusEffect? effect, SpawnInfo? info)
        {
            try
            {
                if (RemoteApply<StatusScope>.InProgress) return;

                if (!CoopSession.IsLive) return;
                if (__instance == null || effect == null) return;

                if (!RemotePlayers.TryFindOwner(__instance._owner, out PlayerId victim)) return;

                // Only what the local player or a creature this machine drives did. A puppet's own
                // potions and casts already exist in its owner's game.
                byte origin = OriginOf(info);
                if (origin == OriginNone) return;

                // Only the prefab carries the guid the far side can resolve.
                StatusEffect? prefab = effect.GetPrefabEffect();
                string id = (prefab != null ? prefab : effect).GUID;
                if (string.IsNullOrEmpty(id)) return;

                if (IsBleeding(id)) return;

                RemoteStatus.ReportStatusEffect(victim, id, origin);
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Status] watching a status effect failed: " + e);
            }
        }

        /// <summary>
        /// Whether the local player, a creature this machine drives, or nobody worth reporting put this effect on.
        /// </summary>
        private static byte OriginOf(SpawnInfo? info)
        {
            if (info == null || !info.HasOwner || info.Owner == null) return OriginNone;

            if (PlayerAccess.TryGetPlayer(out Actor player)
                && info.Owner.Pointer == player.Pointer)
            {
                return DamageReportMessage.OriginPlayer;
            }

            // Driven, not merely claimed: a client-driven claimed creature poisons its player there itself.
            return DamageWatch.IsACreatureThisMachineDrives(info.Owner.TryCast<Actor>())
                ? DamageReportMessage.OriginCreature
                : OriginNone;
        }

        /// <summary>
        /// Whether an effect is the health change's own bleeding, which crosses on the damage report instead.
        /// </summary>
        private static bool IsBleeding(string effectId)
        {
            GameplaySettingsHandler? settings = ProviderAccess.GetGameplaySettingsHandler();
            if (settings == null) return false;

            StatusEffect? bleeding =settings.BleedingStatusEffect;

            return bleeding != null && string.Equals(bleeding.GUID, effectId, StringComparison.Ordinal);
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<StatusEndedMessage>(MessageIds.StatusEnded, (peer, m) =>
            {
                if (CoopSession.State == CoopSessionState.Hosting)
                {
                    if (!CoopSession.TryGetSender(peer, out PlayerId sender)) return;
                    m.Player = sender;
                    CoopSession.SendToClientsExcept(m, peer.Id);
                }
                else if (!CoopSession.IsFromHost()) return;

                TakeEnded(m);
            });
        }

        /// <summary>The local player's own effect ended: to everyone (a client's goes through the host).</summary>
        private static void ReportStatusEnded(in StatusEndedMessage message)
            => CoopSession.SendIfLive(message);
    }
}
