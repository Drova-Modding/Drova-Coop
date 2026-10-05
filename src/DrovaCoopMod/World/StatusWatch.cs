using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.StatusEffects;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Watches the status effects this machine puts on other players' bodies, and tells them.
    ///
    /// An effect applied to a puppet is applied to nobody. Poison, slows, wounds and the rest are put
    /// on by whatever landed the hit, on the machine that landed it - so hitting a remote player poisons
    /// a body with no character behind it. Its owner feels the damage, because the effect ticks health
    /// off the puppet and <see cref="DamageWatch"/> reports every tick as an ordinary blow, but they get
    /// no icon, no duration and no way to know why they are dying. Naming the effect is what turns that
    /// into something their own game can apply properly.
    ///
    /// <c>AddEffectFromSourceIntern</c> is the funnel and it is the private one on purpose: both public
    /// ways in - one effect and a list of them - end here, so patching this catches applications that
    /// patching either public method would miss.
    ///
    /// Bleeding is deliberately not reported. It is the one effect that does not come from an ability
    /// but from the health change itself, and its severity depends on the damage that actually landed -
    /// which only the victim's machine can know. It crosses as a flag on the damage report instead, so
    /// the victim's own game sizes the wound; reporting it here as well would open a second one.
    /// </summary>
    internal static class StatusWatch
    {
        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(StatusEffectHandler), "AddEffectFromSourceIntern",
                typeof(StatusWatch), nameof(AddEffectPostfix));
            Hooking.TryPrefix(harmony, typeof(StatusEffectHandler), "AddEffectFromSourceIntern",
                typeof(StatusWatch), nameof(CopyEffectOnLocalPrefix));

            // Every removal ends here - a cure, a cleanse, an effect running out.
            Hooking.TryPostfix(harmony, typeof(StatusEffectHandler), nameof(StatusEffectHandler.RemoveEffects),
                typeof(StatusWatch), nameof(RemoveEffectsPostfix));
        }

        /// <summary>Effects taken off a teammate's body here because they ended on the teammate. For tests.</summary>
        internal static int EndedApplied { get; private set; }

        /// <summary>
        /// The local player's effect is gone (cured, or run out): their copies elsewhere drop it too. Without this a
        /// poison an antidote ended stayed on the copies - and wherever it ticks there, kept ticking.
        /// </summary>
        private static void RemoveEffectsPostfix(StatusEffectHandler __instance, Il2CppSystem.Collections.Generic.List<StatusEffect> prefabEffects)
        {
            try
            {
                if (RemoteApply<StatusScope>.InProgress || !CoopSession.IsLive || __instance == null || prefabEffects == null) return;
                if (!RemotePlayers.IsLocalPlayer(__instance._owner)) return;

                foreach (StatusEffect prefab in prefabEffects)
                {
                    if (prefab == null || string.IsNullOrEmpty(prefab.GUID) || __instance.HasStatusEffectApplied(prefab)) continue;
                    CoopSession.ReportStatusEnded(new StatusEndedMessage { Player = CoopSession.LocalPlayerId, EffectId = prefab.GUID });
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Status] reporting an ended status effect failed: " + e);
            }
        }

        /// <summary>A teammate's effect ended on them: take it off their body here.</summary>
        internal static void TakeEnded(in StatusEndedMessage message)
        {
            try
            {
                if (!RemotePlayers.Bodies.TryGetValue(message.Player, out RemotePlayer? body) || body?.Body == null) return;

                StatusEffectHandler? handler = body.Body.GetStatusEffectHandler();
                StatusEffect? prefab = ProviderAccess.StatusEffectDatabase?.GetStatusEffectByGuid(message.EffectId);
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

        /// <summary>Effects a teammate's copy tried to put on the player here, refused. For the log and tests.</summary>
        internal static int CopyEffectsRefused { get; private set; }

        /// <summary>
        /// Refuses an effect a teammate's copy puts on the player here. That teammate's own game hit this
        /// player's copy with the same spell and reports the effect, which lands through the report; the copy
        /// replaying the cast here landing it too put it on twice - a poison cry under friendly fire poisoned
        /// its victim twice over (combat_ff.coop). Damage already works this way; status effects did not.
        /// </summary>
        private static bool CopyEffectOnLocalPrefix(StatusEffectHandler __instance, SpawnInfo info)
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
        /// Reports an effect that landed on a remote player's body.
        ///
        /// Runs for every status effect applied to every actor in the game, so the guards are ordered
        /// cheapest first and the common answer costs a session-state comparison.
        /// </summary>
        private static void AddEffectPostfix(StatusEffectHandler __instance, StatusEffect effect, SpawnInfo info)
        {
            try
            {
                // The effect this mod itself just applied, arriving back through the funnel it was always
                // going to arrive back through. Without this the two machines would trade one poison
                // forever, each reading the other's application as a fresh one.
                if (RemoteApply<StatusScope>.InProgress) return;

                if (!CoopSession.IsLive) return;
                if (__instance == null || effect == null) return;

                if (!RemotePlayers.TryFindOwner(__instance._owner, out PlayerId victim)) return;

                // Only what the local player or a creature this machine drives did to them. A puppet
                // applies effects to itself all the time - it drinks the potions its owner drinks and casts
                // the spells its owner casts, because those are replicated - and its owner already has
                // every one of those from their own game. Reporting them would hand the buff back and apply
                // it twice. An effect with no named source is skipped for the same reason: there is nothing
                // to say it came from here.
                //
                // A driven creature is the case that was missing, and it is the same one the damage report
                // was missing: the far copy of that creature never swings, so the poison it puts on a
                // teammate is put on here or nowhere. The victim saw their health fall and never learned
                // why - and with the effect never crossing, they were not actually poisoned at all.
                byte origin = OriginOf(info);
                if (origin == OriginNone) return;

                // The prefab, not the running instance. What is handed to this method is already the
                // authored asset on the ordinary paths, but an effect that arrived by another route can be
                // a live copy, and only the asset carries the guid the far side can resolve.
                StatusEffect prefab = effect.GetPrefabEffect();
                string id = (prefab != null ? prefab : effect).GUID;
                if (string.IsNullOrEmpty(id)) return;

                if (IsBleeding(id)) return;

                CoopSession.ReportStatusEffect(victim, id, origin);
            }
            catch (Exception e)
            {
                // Swallowed rather than allowed out. This runs inside the game's own status effect path,
                // and an exception escaping it would take the effect with it - for every actor in the
                // world, not only for a puppet.
                MelonLogger.Error("[Status] watching a status effect failed: " + e);
            }
        }

        /// <summary>Nothing this machine is entitled to speak for put this effect on.</summary>
        private const byte OriginNone = 0xFF;

        /// <summary>
        /// Who put this effect on, as far as it concerns anybody else.
        ///
        /// Three answers, and the third is the common one: the person playing here, a creature this machine
        /// drives, or nobody worth reporting - the body doing to itself what its owner is already doing, an
        /// NPC both machines are running independently, or an effect with no named source at all.
        /// </summary>
        private static byte OriginOf(SpawnInfo info)
        {
            if (info == null || !info.HasOwner || info.Owner == null) return OriginNone;

            if (PlayerAccess.TryGetPlayer(out Actor player)
                // By pointer, for the usual reason: two proxies over one native object are different
                // managed instances, so the equality operator answers a question about destruction rather
                // than about identity.
                && info.Owner.Pointer == player.Pointer)
            {
                return DamageReportMessage.OriginPlayer;
            }

            // Only a creature this machine drives, by the same rule as its blows. Claimed is not enough: a
            // claimed creature the client drives runs its own copy there, which poisons its player itself,
            // and the host speaking for it as well applied every such effect twice.
            return DamageWatch.IsACreatureThisMachineDrives(info.Owner.TryCast<Actor>())
                ? DamageReportMessage.OriginCreature
                : OriginNone;
        }

        /// <summary>
        /// Whether an effect is the bleeding the health change applies by itself, which crosses as a flag
        /// on the damage report rather than as an effect of its own.
        /// </summary>
        private static bool IsBleeding(string effectId)
        {
            GameplaySettingsHandler? settings = ProviderAccess.GetGameplaySettingsHandler();
            if (settings == null) return false;

            StatusEffect bleeding = settings.BleedingStatusEffect;

            return bleeding != null && string.Equals(bleeding.GUID, effectId, StringComparison.Ordinal);
        }
    }
}
